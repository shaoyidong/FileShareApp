using FileShare.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Core.Network.Mdns;

/// <summary>
/// 最小 mDNS 服务发现（Bonjour 兼容）：作为 UDP 广播发现的补充，在受限网络（隔离 Wi-Fi、禁用子网广播）下提高发现成功率。
/// <para>服务类型：<c>_fileshare._tcp.local</c>。实例名为设备 ID。</para>
/// <para>行为：</para>
/// <list type="bullet">
/// <item>启动时组播公告自身服务（PTR/SRV/TXT/A 记录），并周期性重发以维持存在感。</item>
/// <item>收到对 _fileshare._tcp.local 的 PTR 查询时回应自身记录。</item>
/// <item>收到对端公告/响应时解析其实例信息，通过 OnDeviceDiscovered 上报（与 UDP 发现产出等价的 DeviceInfo）。</list>
/// <para>纯 UDP 多播 + 字节编解码，无外部依赖，AOT 安全。</para>
/// </summary>
public sealed class MdnsService : IDisposable
{
    private const string MulticastAddress = "224.0.0.251";
    private const int MulticastPort = 5353;
    private const string ServiceType = "_fileshare._tcp.local";
    private const int AnnounceIntervalMs = 15000; // 周期性公告间隔
    private const int DeviceExpirySeconds = 45;   // mDNS 设备过期时间（略长于公告间隔，容忍丢包）

    private readonly DeviceInfo _localDevice;
    private readonly string _instanceName;  // <deviceId>._fileshare._tcp.local
    private readonly string _hostName;       // <deviceId>.local
    private readonly IPAddress _localIp;
    private readonly ILogger<MdnsService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _seenPeers = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private UdpClient? _udpClient;
    private CancellationTokenSource _cts = new();
    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    /// <summary>发现设备时触发（产出与 UDP 发现等价的 DeviceInfo）。</summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;

    /// <summary>设备过期离线时触发。</summary>
    public event Action<DeviceInfo>? OnDeviceRemoved;

    public MdnsService(DeviceInfo localDevice, ILogger<MdnsService>? logger = null)
    {
        _localDevice = localDevice;
        _logger = logger ?? NullLogger<MdnsService>.Instance;
        var safeId = SanitizeLabel(localDevice.DeviceId);
        _instanceName = $"{safeId}.{ServiceType}";
        _hostName = $"{safeId}.local";
        _localIp = ParseLocalIp(localDevice.IpAddress);
    }

    /// <summary>启动 mDNS 服务：加入多播组，发送初始公告 + 查询，开启监听与周期公告。</summary>
    public async Task<bool> StartAsync()
    {
        if (_isRunning || _isDisposed) return false;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            _udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
            // 限制多播 TTL=255（mDNS 规范），避免跨子网泄漏
            try { _udpClient.Ttl = 255; } catch { /* 部分平台不支持，忽略 */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS 绑定多播组失败（端口 {Port} 可能被占用），mDNS 发现不可用", MulticastPort);
            _udpClient?.Dispose();
            _udpClient = null;
            return false;
        }

        _isRunning = true;
        _cts = new CancellationTokenSource();
        _logger.LogInformation("mDNS 服务发现已启动，服务类型 {ServiceType}", ServiceType);

        // 初始公告 + 主动查询对端
        _ = Task.Run(async () =>
        {
            await SendAnnouncementAsync().ConfigureAwait(false);
            await SendServiceQueryAsync().ConfigureAwait(false);
        });

        // 监听循环
        _ = Task.Run(() => ListenAsync(_cts.Token));
        // 周期公告 + 过期清理
        _ = Task.Run(() => AnnounceLoopAsync(_cts.Token));

        return true;
    }

    /// <summary>停止 mDNS 服务。</summary>
    public async Task StopAsync()
    {
        if (!_isRunning || _isDisposed) return;
        _isRunning = false;
        _cts.Cancel();

        // 发送一条 TTL=0 的公告（goodbye），让对端尽快移除本设备
        try { await SendGoodbyeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }

        await Task.Delay(50).ConfigureAwait(false);
        ReleaseResources();
        _logger.LogInformation("mDNS 服务发现已停止");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                    _logger.LogWarning(ex, "mDNS 接收失败");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 忽略自身发出的多播包（源 IP 为本机）
            if (result.RemoteEndPoint.Address.Equals(_localIp)) continue;

            var msg = MdnsCodec.Decode(result.Buffer, result.Buffer.Length);
            if (msg == null) continue;

            try
            {
                HandleMessage(msg, result.RemoteEndPoint.Address);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "处理 mDNS 报文失败");
            }
        }
    }

    private void HandleMessage(MdnsMessage msg, IPAddress remoteIp)
    {
        // 响应报文：解析对端服务记录
        if (msg.IsResponse)
        {
            HandlePeerResponse(msg, remoteIp);
            return;
        }

        // 查询报文：若询问本服务类型或本实例，回应自身记录
        bool wantServiceType = false;
        bool wantOurInstance = false;
        foreach (var q in msg.Questions)
        {
            if (string.Equals(q.Name, ServiceType, StringComparison.OrdinalIgnoreCase) && q.Type == MdnsCodec.TypePTR)
                wantServiceType = true;
            if (string.Equals(q.Name, _instanceName, StringComparison.OrdinalIgnoreCase))
                wantOurInstance = true;
            if (string.Equals(q.Name, _hostName, StringComparison.OrdinalIgnoreCase) && q.Type == MdnsCodec.TypeA)
                wantOurInstance = true;
        }

        if (wantServiceType || wantOurInstance)
        {
            _ = Task.Run(() => SendAnnouncementAsync().ConfigureAwait(false));
        }
    }

    private void HandlePeerResponse(MdnsMessage msg, IPAddress remoteIp)
    {
        string? instanceName = null;
        int port = 0;
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IPAddress? aRecordIp = null;

        foreach (var rr in msg.Answers)
        {
            // PTR：_fileshare._tcp.local → <instance>._fileshare._tcp.local
            if (rr.Type == MdnsCodec.TypePTR &&
                string.Equals(rr.Name, ServiceType, StringComparison.OrdinalIgnoreCase))
            {
                instanceName = MdnsCodec.ParsePtr(rr.Rdata);
            }
            else if (rr.Type == MdnsCodec.TypeSRV &&
                     rr.Name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase))
            {
                instanceName = rr.Name;
                var srv = MdnsCodec.ParseSrv(rr.Rdata);
                port = srv.Port;
            }
            else if (rr.Type == MdnsCodec.TypeTXT &&
                     rr.Name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase))
            {
                instanceName = rr.Name;
                foreach (var kv in MdnsCodec.ParseTxt(rr.Rdata)) txt[kv.Key] = kv.Value;
            }
            else if (rr.Type == MdnsCodec.TypeA &&
                     rr.Name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                aRecordIp = MdnsCodec.ParseA(rr.Rdata);
            }
        }

        if (string.IsNullOrEmpty(instanceName)) return;

        // 提取实例标签作为 deviceId
        var instanceLabel = instanceName.Split('.', 2)[0];
        if (string.IsNullOrEmpty(instanceLabel)) return;
        if (instanceLabel == SanitizeLabel(_localDevice.DeviceId) &&
            instanceName.Equals(_instanceName, StringComparison.OrdinalIgnoreCase)) return; // 自身，忽略

        // 从 TXT 还原设备信息；IP 优先用 A 记录，回退到源地址
        if (!txt.TryGetValue("id", out var deviceId)) deviceId = instanceLabel;
        if (!txt.TryGetValue("name", out var deviceName)) deviceName = instanceLabel;
        if (!Enum.TryParse<DeviceType>(txt.GetValueOrDefault("type"), true, out var deviceType))
            deviceType = DeviceType.Desktop;
        var supportsTls = txt.TryGetValue("tls", out var tlsVal) && tlsVal == "1";
        var ip = (aRecordIp?.ToString() ?? remoteIp.ToString());

        var device = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            DeviceType = deviceType,
            IpAddress = ip,
            Port = port,
            SupportsTls = supportsTls,
            LastSeen = DateTime.Now
        };

        // 过期清理基于 LastSeen；首次或更新都上报
        _seenPeers[device.DeviceId] = DateTime.Now;
        OnDeviceDiscovered?.Invoke(device);
    }

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            try
            {
                await Task.Delay(AnnounceIntervalMs, cancellationToken).ConfigureAwait(false);
                await SendAnnouncementAsync().ConfigureAwait(false);
                CleanupExpiredPeers();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mDNS 周期公告失败");
            }
        }
    }

    private void CleanupExpiredPeers()
    {
        var cutoff = DateTime.Now.AddSeconds(-DeviceExpirySeconds);
        foreach (var kv in _seenPeers)
        {
            if (kv.Value < cutoff)
            {
                _seenPeers.TryRemove(kv.Key, out _);
                // 无完整的 DeviceInfo 缓存（仅存时间戳），OnDeviceRemoved 携带 deviceId 标识的占位
                OnDeviceRemoved?.Invoke(new DeviceInfo
                {
                    DeviceId = kv.Key,
                    DeviceName = kv.Key,
                    IpAddress = "",
                    Port = 0
                });
            }
        }
    }

    /// <summary>发送自身服务公告（PTR/SRV/TXT/A）。</summary>
    private Task SendAnnouncementAsync() => SendAnnouncementCoreAsync(goodbye: false);

    /// <summary>发送 goodbye 公告（TTL=0），通知对端移除本设备。</summary>
    private Task SendGoodbyeAsync() => SendAnnouncementCoreAsync(goodbye: true);

    private async Task SendAnnouncementCoreAsync(bool goodbye)
    {
        if (_udpClient == null) return;

        var msg = new MdnsMessage
        {
            Id = 0,
            Flags = 0x8400 // QR=1（响应）, AA=1（权威应答）
        };

        var ttl = goodbye ? 0u : 4500u;

        // PTR: _fileshare._tcp.local → <instance>._fileshare._tcp.local
        msg.Answers.Add(new MdnsRecord
        {
            Name = ServiceType,
            Type = MdnsCodec.TypePTR,
            Class = MdnsCodec.ClassIN,
            Ttl = ttl,
            Rdata = MdnsCodec.BuildPtr(_instanceName)
        });

        // SRV: <instance> → port + <host>
        msg.Answers.Add(new MdnsRecord
        {
            Name = _instanceName,
            Type = MdnsCodec.TypeSRV,
            Class = MdnsCodec.ClassCacheFlush,
            Ttl = ttl,
            Rdata = MdnsCodec.BuildSrv(0, 0, _localDevice.Port, _hostName)
        });

        // TXT: 设备属性
        var txt = new Dictionary<string, string>
        {
            ["id"] = _localDevice.DeviceId,
            ["name"] = _localDevice.DeviceName,
            ["type"] = _localDevice.DeviceType.ToString(),
            ["tls"] = _localDevice.SupportsTls ? "1" : "0"
        };
        msg.Answers.Add(new MdnsRecord
        {
            Name = _instanceName,
            Type = MdnsCodec.TypeTXT,
            Class = MdnsCodec.ClassCacheFlush,
            Ttl = ttl,
            Rdata = MdnsCodec.BuildTxt(txt)
        });

        // A: <host> → 本机 IP
        if (!_localIp.Equals(IPAddress.None))
        {
            msg.Answers.Add(new MdnsRecord
            {
                Name = _hostName,
                Type = MdnsCodec.TypeA,
                Class = MdnsCodec.ClassCacheFlush,
                Ttl = ttl,
                Rdata = MdnsCodec.BuildA(_localIp)
            });
        }

        var data = MdnsCodec.Encode(msg);
        await SendMulticastAsync(data).ConfigureAwait(false);
    }

    /// <summary>主动查询 _fileshare._tcp.local 的 PTR，触发对端回应。</summary>
    private async Task SendServiceQueryAsync()
    {
        if (_udpClient == null) return;

        var msg = new MdnsMessage { Id = 0, Flags = 0x0000 };
        msg.Questions.Add(new MdnsQuestion
        {
            Name = ServiceType,
            Type = MdnsCodec.TypePTR,
            Class = MdnsCodec.ClassIN
        });

        var data = MdnsCodec.Encode(msg);
        await SendMulticastAsync(data).ConfigureAwait(false);
    }

    private async Task SendMulticastAsync(byte[] data)
    {
        if (_isDisposed || _udpClient == null) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isDisposed && _udpClient != null)
            {
                await _udpClient.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string SanitizeLabel(string deviceId)
    {
        // 设备 ID 通常是 GUID，但防御性处理 DNS 标签非法字符
        var sb = new StringBuilder(deviceId.Length);
        foreach (var c in deviceId)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        }
        return sb.ToString();
    }

    private static IPAddress ParseLocalIp(string ipAddress)
    {
        return IPAddress.TryParse(ipAddress, out var ip) ? ip : IPAddress.None;
    }

    private void ReleaseResources()
    {
        if (_udpClient != null)
        {
            try
            {
                _udpClient.DropMulticastGroup(IPAddress.Parse(MulticastAddress));
                _udpClient.Close();
                _udpClient.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放 mDNS UdpClient 失败");
            }
            finally { _udpClient = null; }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { _cts.Cancel(); } catch { }
        ReleaseResources();
        _cts.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
