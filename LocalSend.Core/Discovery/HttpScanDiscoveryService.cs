using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LocalSend.Core.Http.Client;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.State;
using LocalSend.Core.Model;
using ProtocolType = LocalSend.Core.Http.Dto.ProtocolType;

namespace LocalSend.Core.Discovery;

/// <summary>
/// 通过 HTTP 子网扫描发现局域网 LocalSend 设备。
/// 对应 Dart 端 <c>http_scan_discovery.dart</c> + <c>http_target_discovery.dart</c>。
/// </summary>
/// <remarks>
/// 这正是原版 LocalSend "Search devices" 按钮背后的机制：
/// 遍历本机所在 /24 子网内的所有 IP（排除自己），并发向每个 IP 的
/// <c>POST /api/localsend/v2/register</c> 发请求，能成功响应的就是 LocalSend 设备。
/// 当 UDP 组播因防火墙/路由/虚拟网卡等原因失效时，这是最可靠的保底发现方式。
/// </remarks>
public sealed class HttpScanDiscoveryService
{
    /// <summary>并发请求数，对齐 Dart 端 TaskRunner 的 concurrency: 50。</summary>
    public const int DefaultConcurrency = 50;

    /// <summary>单个 IP 的请求超时；超时视为该 IP 无 LocalSend 服务。</summary>
    public static readonly TimeSpan DefaultPerRequestTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly LsHttpClientV2 _httpClient;
    private readonly ClientInfo _localInfo;
    private readonly ushort _port;
    private readonly ProtocolTypeV2 _protocol;
    private readonly string _ownFingerprint;
    private readonly int _concurrency;
    private readonly TimeSpan _perRequestTimeout;

    /// <summary>新设备发现事件（每发现一台触发一次，便于实时输出）。</summary>
    public event EventHandler<MulticastPeerInfo>? DeviceDiscovered;

    /// <summary>
    /// 创建 HTTP 子网扫描服务。
    /// </summary>
    /// <param name="httpClient">带 mTLS 证书的 HTTP 客户端（用于连接 HTTPS 模式的原版 LocalSend）。</param>
    /// <param name="localInfo">本机信息。</param>
    /// <param name="port">目标端口（默认 53317）。</param>
    /// <param name="protocol">连接对端使用的协议（原版 LocalSend 默认 HTTPS）。</param>
    /// <param name="ownFingerprint">本机指纹，用于过滤自己；默认取 <paramref name="localInfo"/>.Token。</param>
    /// <param name="concurrency">并发数。</param>
    /// <param name="perRequestTimeout">单请求超时。</param>
    public HttpScanDiscoveryService(
        LsHttpClientV2 httpClient,
        ClientInfo localInfo,
        ushort port,
        ProtocolTypeV2 protocol = ProtocolTypeV2.Https,
        string? ownFingerprint = null,
        int? concurrency = null,
        TimeSpan? perRequestTimeout = null)
    {
        _httpClient = httpClient;
        _localInfo = localInfo;
        _port = port;
        _protocol = protocol;
        _ownFingerprint = ownFingerprint ?? localInfo.Token;
        _concurrency = concurrency ?? DefaultConcurrency;
        _perRequestTimeout = perRequestTimeout ?? DefaultPerRequestTimeout;
    }

    /// <summary>
    /// 扫描本机所有 Up 状态 IPv4 接口所在的 /24 子网。
    /// 对每个子网内非本机 IP 并发发送 /register 请求。
    /// </summary>
    public async Task<List<MulticastPeerInfo>> ScanAllSubnetsAsync(CancellationToken ct = default)
    {
        var localIps = GetLocalIPv4Addresses();
        Console.WriteLine($"    [HTTP扫描] 本机 IPv4 接口: {string.Join(", ", localIps)}");

        // 用指纹去重（同一设备可能从多个子网被发现）
        var allFound = new ConcurrentDictionary<string, MulticastPeerInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var localIp in localIps)
        {
            if (ct.IsCancellationRequested) break;
            var found = await ScanSubnetAsync(localIp, ct).ConfigureAwait(false);
            foreach (var d in found)
            {
                allFound.TryAdd(d.Fingerprint, d);
            }
        }
        return allFound.Values.ToList();
    }

    /// <summary>
    /// 扫描指定本机 IP 所在的 /24 子网（如 192.168.1.50 → 扫描 192.168.1.0~255，排除自己）。
    /// </summary>
    public async Task<List<MulticastPeerInfo>> ScanSubnetAsync(IPAddress localIp, CancellationToken ct = default)
    {
        var subnetBase = GetSubnetBase(localIp);
        if (subnetBase == null)
        {
            Console.WriteLine($"    [HTTP扫描] 无法计算 {localIp} 的 /24 子网，跳过");
            return new List<MulticastPeerInfo>();
        }

        var localIpStr = localIp.ToString();
        // 生成 /24 子网内 256 个 IP，排除本机
        var targets = new List<string>(255);
        var bytes = subnetBase.GetAddressBytes();
        for (int i = 0; i < 256; i++)
        {
            bytes[3] = (byte)i;
            var ip = new IPAddress(bytes).ToString();
            if (ip != localIpStr) targets.Add(ip);
        }

        Console.WriteLine($"    [HTTP扫描] 扫描 {subnetBase}/24（{targets.Count} 个 IP，并发 {_concurrency}，单请求超时 {_perRequestTimeout.TotalMilliseconds:F0}ms，协议={_protocol.AsStr()})...");

        var found = new ConcurrentBag<MulticastPeerInfo>();
        using var sem = new SemaphoreSlim(_concurrency, _concurrency);
        var tasks = targets.Select(async ip =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_perRequestTimeout);
                var device = await TryDiscoverAsync(ip, cts.Token).ConfigureAwait(false);
                if (device != null)
                {
                    found.Add(device);
                    DeviceDiscovered?.Invoke(this, device);
                    Console.WriteLine($"    [HTTP扫描]   ✓ 发现 {device}");
                }
            }
            catch
            {
                // 单个 IP 失败（超时/拒绝/TLS 握手失败）属正常情况，忽略
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Console.WriteLine($"    [HTTP扫描] {subnetBase}/24 扫描完成，本子网发现 {found.Count} 台设备");
        return found.ToList();
    }

    /// <summary>
    /// 对单个 IP 发送 /register 请求。
    /// 对应 Dart 端 <c>HttpTargetDiscoveryService.discover</c>：
    /// 任何异常都吞掉返回 null；响应指纹等于本机指纹时视为发现自己，返回 null。
    /// </summary>
    private async Task<MulticastPeerInfo?> TryDiscoverAsync(string ip, CancellationToken ct)
    {
        var payload = new RegisterDtoV2
        {
            Alias = _localInfo.Alias,
            Version = _localInfo.Version,
            DeviceType = _localInfo.DeviceType,
            Fingerprint = _ownFingerprint,
            Port = _port,
            Protocol = _protocol,
            Download = false
        };

        var protocol = _protocol == ProtocolTypeV2.Https ? ProtocolType.Https : ProtocolType.Http;
        var result = await _httpClient.RegisterAsync(
            protocol: protocol,
            ip: ip,
            port: _port,
            payload: payload,
            ct: ct).ConfigureAwait(false);

        // 发现自己（对端指纹等于本机指纹）→ 跳过，对应 Dart 中 response.body.token == _fingerprint
        if (string.Equals(result.Body.Fingerprint, _ownFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MulticastPeerInfo
        {
            Alias = result.Body.Alias,
            Version = result.Body.Version,
            DeviceModel = result.Body.DeviceModel,
            DeviceType = result.Body.DeviceType,
            Fingerprint = result.Body.Fingerprint,
            Port = _port,                      // 响应体不含 port，沿用本机端口（原版默认 53317）
            Protocol = _protocol,              // 沿用我们连接对端用的协议
            Download = result.Body.Download,
            IP = IPAddress.Parse(ip)
        };
    }

    /// <summary>枚举本机所有 Up 状态、非环回的 IPv4 地址。</summary>
    private static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.Add(addr.Address);
                }
            }
        }
        return result;
    }

    /// <summary>取 /24 子网基址（清零最后一段）。</summary>
    private static IPAddress? GetSubnetBase(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var bytes = ip.GetAddressBytes();
        bytes[3] = 0;
        return new IPAddress(bytes);
    }
}
