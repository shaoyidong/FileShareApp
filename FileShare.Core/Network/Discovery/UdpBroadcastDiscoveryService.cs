using FileShare.Core.Common;
using FileShare.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// UDP设备发现服务
/// </summary>
public class UdpBroadcastDiscoveryService : IDeviceDiscoveryService
{   
    private const string DiscoveryMessage = "FileShareDiscovery";
    private const string GoodbyeMessage = "FileShareGoodbye";

    // 广播间隔配置
    private const int MaxBroadcastIntervalMs = 15000; // 最大广播间隔
    private const int StableBroadcastIntervalMs = 10000; // 稳定状态广播间隔
    // 设备过期时间
    private const int DeviceExpirySeconds = 45;

    // 回应节流
    private const int MinResponseIntervalMs = 500;

    private const int MaxFailedBroadcasts = 3; // 最大失败次数阈值        

    private readonly int _discoveryPort;
    private readonly IPEndPoint _broadcastEndPoint;
    private readonly IPAddress _localIp;
    private readonly DeviceInfo _localDevice;
    private readonly ILogger<UdpBroadcastDiscoveryService> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastResponseTimes = new();

    // 网络拥塞检测
    private int _failedBroadcasts = 0; // 失败的广播次数

    private UdpClient? _udpClient;

    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private CancellationTokenSource? _cts;

    // 后台循环任务的控制
    private Task? _announceLoopTask;

    /// <summary>
    /// 发现设备时触发
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;

    /// <summary>
    /// 设备离线时触发
    /// </summary>
    public event Action<string>? OnDeviceRemoved;

    public UdpBroadcastDiscoveryService(DeviceInfo localDevice, int udpBroadcastPort, ILoggerFactory? loggerFactory = null)
    {
        _localDevice = localDevice;
        _logger = loggerFactory?.CreateLogger<UdpBroadcastDiscoveryService>() ?? NullLogger<UdpBroadcastDiscoveryService>.Instance;
        _localIp = ParseLocalIp(localDevice.IpAddress);
        _broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, udpBroadcastPort);
        _discoveryPort = udpBroadcastPort;
    }

    /// <summary>
    /// 启动服务：绑定端口，开始监听，并执行快速发现（连续发送几次广播）
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_isRunning || _isDisposed)
            return false;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.EnableBroadcast = true;
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "绑定UDP端口 {Port} 失败，发现服务不可用", _discoveryPort);
            _udpClient?.Dispose();
            _udpClient = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        _logger.LogInformation("UDP发现服务已启动，端口 {Port}", _discoveryPort);

        // 初始公告
        _ = Task.Run(async () =>
        {
            await SendAnnouncementAsync(_cts.Token).ConfigureAwait(false);
        });

        // 启动监听任务（始终运行）
        _ = Task.Run(() => ListenAsync(_cts.Token));

        _isRunning = true;
        return true;
    }

    /// <summary>
    /// 停止服务，发送Goodbye（可选）并释放资源
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning || _isDisposed)
            return;

        _isRunning = false;
        _cts?.Cancel();

        try { await SendGoodbyeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }

        if (_announceLoopTask != null)
        {
            try { await _announceLoopTask.ConfigureAwait(false); } catch { }
            _announceLoopTask = null;
        }

        await Task.Delay(50).ConfigureAwait(false);

        ReleaseResources();
        _logger.LogInformation("UDP发现服务已停止");
    }

    public async Task SendServiceQueryAsync()
    {
        await SendAnnouncementAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task StartAnnounceLoopAsync()
    {
        if (!_isRunning || _isDisposed)
            return;

        // 如果已有周期性任务则无需重复启动
        if (_announceLoopTask != null && !_announceLoopTask.IsCompleted)
            return;

        _announceLoopTask = Task.Run(() => AnnounceLoopAsync(_cts?.Token ?? CancellationToken.None));
    }

    /// <summary>
    /// 监听广播并回应
    /// </summary>
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
                    _logger.LogWarning(ex, "接收广播消息失败");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (result.RemoteEndPoint.Address.Equals(_localIp))
                continue;

            var msg = Encoding.UTF8.GetString(result.Buffer);
            if (string.IsNullOrEmpty(msg))
                continue;

            try
            {
                HandleMessage(msg, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "处理广播报文失败");
            }
        }
    }

    /// <summary>
    /// 后台周期性广播
    /// </summary>
    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        int interval = StableBroadcastIntervalMs;
        _failedBroadcasts = 0;

        while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
        {
            try
            {
                await SendAnnouncementAsync(cancellationToken).ConfigureAwait(false);

                _failedBroadcasts = 0;
                // 动态调整间隔：根据发现的设备数量
                int deviceCount = _lastResponseTimes.Count;
                if (deviceCount == 0)
                    interval = Math.Min(StableBroadcastIntervalMs, interval);
                else if (deviceCount <= 1)
                    interval = StableBroadcastIntervalMs;
                else
                    interval = Math.Min(StableBroadcastIntervalMs * 2, MaxBroadcastIntervalMs);

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                {
                    _logger.LogWarning(ex, "周期性广播失败");
                    _failedBroadcasts++;

                    // 网络拥塞检测
                    if (_failedBroadcasts >= MaxFailedBroadcasts)
                    {
                        interval = Math.Min(interval * 2, MaxBroadcastIntervalMs);
                        _logger.LogWarning("广播连续失败，增加间隔至 {Interval}ms", interval);
                        _failedBroadcasts = 0;
                    }
                }
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async void HandleMessage(string msg, IPEndPoint remoteIp)
    {
        // ---- 处理下线消息 ----
        if (msg.StartsWith(GoodbyeMessage, StringComparison.Ordinal))
        {
            var json = msg.Substring(GoodbyeMessage.Length);
            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(json, SourceGenerationContext.Default.DeviceInfo);
            if (deviceInfo != null &&
                !string.IsNullOrEmpty(deviceInfo.DeviceId) &&
                deviceInfo.DeviceId != _localDevice.DeviceId)
            {
                OnDeviceRemoved?.Invoke(deviceInfo.DeviceId);
            }
        }
        else if (msg.StartsWith(DiscoveryMessage, StringComparison.Ordinal))
        {
            // 解析设备信息
            var json = msg.Substring(DiscoveryMessage.Length);
            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(json, SourceGenerationContext.Default.DeviceInfo);

            if (deviceInfo != null
                && !string.IsNullOrEmpty(deviceInfo.DeviceId)
                && deviceInfo.DeviceId != _localDevice.DeviceId
                && !string.IsNullOrEmpty(deviceInfo.DeviceName))
            {
                deviceInfo.IpAddress = remoteIp.Address.ToString();
                deviceInfo.LastSeen = DateTime.Now;

                OnDeviceDiscovered?.Invoke(deviceInfo);

                await SendResponseWithThrottlingAsync(remoteIp).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 带节流的发送回应消息
    /// </summary>
    private async Task SendResponseWithThrottlingAsync(IPEndPoint remoteEndPoint)
    {
        var deviceKey = remoteEndPoint.Address.ToString();
        var now = DateTime.Now;

        if (_lastResponseTimes.TryGetValue(deviceKey, out var last))
        {
            if ((now - last).TotalMilliseconds < MinResponseIntervalMs)
                return;
        }
        _lastResponseTimes[deviceKey] = now;
        await SendResponseAsync(remoteEndPoint).ConfigureAwait(false);
        CleanupExpiredResponseTimes();
    }

    /// <summary>
    /// 快速发送广播
    /// </summary>
    private async Task SendAnnouncementAsync(CancellationToken cancellationToken)
    {
        if (!_isRunning || _isDisposed || _udpClient == null)
            return;

        try
        {
            var data = BuildDiscoveryPacket();
            await SendToAllBroadcastsAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送发现数据包失败");
        }
    }

    /// <summary>
    /// 发送下线通知（Goodbye），告知其他设备本机即将离线。
    /// </summary>
    public async Task SendGoodbyeAsync()
    {
        if (!_isRunning || _isDisposed || _udpClient == null) return;
        try
        {
            var data = BuildGoodbyePacket();
            await SendToAllBroadcastsAsync(data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
        }
    }

    private byte[] BuildDiscoveryPacket()
    {
        var deviceJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
        var message = DiscoveryMessage + deviceJson;
        return Encoding.UTF8.GetBytes(message);
    }

    private byte[] BuildGoodbyePacket()
    {
        var deviceJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
        var message = GoodbyeMessage + deviceJson;
        return Encoding.UTF8.GetBytes(message);
    }

    /// <summary>
    /// 发送回应消息（单播到已发现的对端）
    /// </summary>
    private async Task SendResponseAsync(IPEndPoint remoteEndPoint)
    {
        if (_isDisposed || _udpClient == null) return;
        try
        {
            var data = BuildDiscoveryPacket();
            await SendLockedAsync(data, remoteEndPoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送回应失败");
        }
    }

    private void CleanupExpiredResponseTimes()
    {
        var now = DateTime.Now;
        var cutoff = now.AddSeconds(-DeviceExpirySeconds);

        // 清理过期的回应时间记录
        foreach (var kvp in _lastResponseTimes)
        {
            if ((now - kvp.Value).TotalSeconds > DeviceExpirySeconds)
                _lastResponseTimes.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// 向所有活跃网卡的子网广播地址发送数据包（多网卡支持）
    /// </summary>
    private async Task SendToAllBroadcastsAsync(byte[] data, CancellationToken cancellationToken)
    {
        var endpoints = GetBroadcastEndpoints();
        foreach (var endpoint in endpoints)
            await SendLockedAsync(data, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLockedAsync(byte[] data, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (_isDisposed || _udpClient == null) return;
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isDisposed && _udpClient != null)
                await _udpClient.SendAsync(data, data.Length, endpoint).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 枚举所有活跃网卡的子网定向广播地址，并附加有限广播作为兜底
    /// </summary>
    private List<IPEndPoint> GetBroadcastEndpoints()
    {
        var endpoints = new List<IPEndPoint>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = addr.IPv4Mask;
                    if (mask == null) continue;

                    var broadcast = GetDirectedBroadcast(addr.Address, mask);
                    if (broadcast != null)
                        endpoints.Add(new IPEndPoint(broadcast, _discoveryPort));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举网卡广播地址失败");
        }

        // 始终包含有限广播作为兜底（部分平台/驱动不发送定向广播）
        endpoints.Add(_broadcastEndPoint);
        return endpoints.Distinct().ToList();
    }

    /// <summary>
    /// 根据IP和子网掩码计算定向广播地址 (ip | ~mask)
    /// </summary>
    private static IPAddress? GetDirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var addrBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addrBytes.Length != 4 || maskBytes.Length != 4) return null;
        var bcast = new byte[4];
        for (int i = 0; i < 4; i++)
            bcast[i] = (byte)(addrBytes[i] | (byte)~maskBytes[i]);
        return new IPAddress(bcast);
    }

    private static IPAddress ParseLocalIp(string ipAddress)
    {
        return IPAddress.TryParse(ipAddress, out var ip) ? ip : IPAddress.None;
    }

    private void ReleaseResources()
    {
        if (_udpClient != null)
        {
            try { _udpClient.Close(); _udpClient.Dispose(); } catch { }
            _udpClient = null;
        }
    }

    /// <summary>
    /// 释放资源（重载）
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _cts?.Cancel();
                ReleaseResources();
                _cts?.Dispose();
                _sendLock.Dispose();
            }
            _isDisposed = true;
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 析构函数
    /// </summary>
    ~UdpBroadcastDiscoveryService()
    {
        Dispose(false);
    }
}