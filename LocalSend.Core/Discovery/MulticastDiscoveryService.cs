using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalSend.Core.Http.Client;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.State;
using LocalSend.Core.Model;
using ProtocolType = LocalSend.Core.Http.Dto.ProtocolType;

namespace LocalSend.Core.Discovery;

/// <summary>
/// 通过 UDP 组播发现局域网内的 LocalSend 设备。
/// 对应 Flutter/Dart 端 <c>multicast_discovery.dart</c>。
/// </summary>
/// <remarks>
/// 协议细节：
/// <list type="bullet">
///   <item>组播地址：224.0.0.167（224.0.0.0/24 段，兼容 Android）</item>
///   <item>端口：与 HTTP 服务器相同（默认 53317）</item>
///   <item>消息格式：JSON（MulticastMessageV2），通过 UTF-8 编码</item>
///   <item>announce=true 时触发其他设备响应</item>
/// </list>
/// </remarks>
public sealed class MulticastDiscoveryService : IDisposable
{
    public const string DefaultMulticastGroup = "224.0.0.167";
    public const int DefaultMulticastPort = 53317;
    private const string ProtocolVersion = "2.1";

    private readonly string _multicastGroup;
    private readonly int _multicastPort;
    private readonly ClientInfo _localInfo;
    private readonly ProtocolTypeV2 _protocol;
    private readonly ushort _httpPort;
    private readonly string _ownFingerprint;
    private readonly bool _serverRunning;
    private readonly LsHttpClientV2? _httpClient;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _listenCts;
    private readonly ConcurrentDictionary<string, MulticastPeerInfo> _discovered = new();

    /// <summary>
    /// 本机所有可用于组播的 IPv4 地址（用于多接口发送）。
    /// </summary>
    private List<IPAddress> _localIPv4Addresses = new();

    /// <summary>
    /// 已发现的设备（通过 UDP 组播收到）。
    /// </summary>
    public IReadOnlyCollection<MulticastPeerInfo> DiscoveredDevices => _discovered.Values.ToList().AsReadOnly();

    /// <summary>
    /// 新设备发现事件。
    /// </summary>
    public event EventHandler<MulticastPeerInfo>? DeviceDiscovered;

    /// <summary>
    /// 创建组播发现服务。
    /// </summary>
    /// <param name="localInfo">本机信息（别名、版本、设备类型、指纹等）。</param>
    /// <param name="httpPort">HTTP 服务器监听端口。</param>
    /// <param name="protocol">使用的协议（HTTP/HTTPS）。</param>
    /// <param name="multicastGroup">组播地址，默认 224.0.0.167。</param>
    /// <param name="multicastPort">组播端口，默认 53317。</param>
    /// <param name="serverRunning">本机 HTTP 服务器是否在运行（决定是否自动响应 announce）。</param>
    /// <param name="httpClient">用于 TCP 响应的 HTTP 客户端（为 null 则用 UDP 响应）。</param>
    public MulticastDiscoveryService(
        ClientInfo localInfo,
        ushort httpPort,
        ProtocolTypeV2 protocol = ProtocolTypeV2.Http,
        string multicastGroup = DefaultMulticastGroup,
        int multicastPort = DefaultMulticastPort,
        bool serverRunning = true,
        LsHttpClientV2? httpClient = null)
    {
        _localInfo = localInfo;
        _httpPort = httpPort;
        _protocol = protocol;
        _multicastGroup = multicastGroup;
        _multicastPort = multicastPort;
        _ownFingerprint = localInfo.Token;
        _serverRunning = serverRunning;
        _httpClient = httpClient;
    }

    // -----------------------------------------------------------------------
    // 网络接口枚举：对齐 Dart 端 _getSockets 的行为
    // -----------------------------------------------------------------------

    /// <summary>
    /// 枚举所有处于 Up 状态、支持组播的非环回 IPv4 地址。
    /// 对应 Dart 端 <c>getNetworkInterfaces</c> + <c>_getSockets</c> 中的接口过滤。
    /// </summary>
    private static List<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (!nic.SupportsMulticast) continue;
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

    /// <summary>
    /// 在所有 IPv4 接口上向组播组发送数据。
    /// 对应 Dart 端 <c>sendAnnouncement</c> 中为每个接口创建 socket 并发送的逻辑。
    /// </summary>
    private async Task SendMulticastOnAllInterfacesAsync(byte[] data, CancellationToken ct)
    {
        if (_udpClient == null) return;
        var ep = new IPEndPoint(IPAddress.Parse(_multicastGroup), _multicastPort);

        foreach (var localAddr in _localIPv4Addresses)
        {
            try
            {
                // 设置 outgoing multicast interface，确保从该接口发出
                _udpClient.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    localAddr.GetAddressBytes());
                await _udpClient.SendAsync(data, data.Length, ep);
            }
            catch (Exception)
            {
                // 某些虚拟接口可能发送失败，忽略
            }
        }
    }

    // -----------------------------------------------------------------------
    // 公共 API
    // -----------------------------------------------------------------------

    /// <summary>
    /// 启动组播监听（后台线程）。
    /// </summary>
    public async Task StartListeningAsync(CancellationToken ct = default)
    {
        if (_udpClient != null)
            throw new InvalidOperationException("Already listening");

        _listenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // 枚举本机所有可用的 IPv4 地址
            _localIPv4Addresses = GetLocalIPv4Addresses();
            var multicastAddr = IPAddress.Parse(_multicastGroup);

            // 用 UdpClient(AddressFamily) 创建，通过 Client 设置 ReuseAddress 等选项
            _udpClient = new UdpClient(AddressFamily.InterNetwork);
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _multicastPort));

            // 在每个 IPv4 接口上加入组播组
            // 对应 Dart 端 socket.joinMulticast(group, interface)
            Console.WriteLine($"    [组播] 本机 IPv4 接口:");
            foreach (var localAddr in _localIPv4Addresses)
            {
                try
                {
                    _udpClient.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(multicastAddr, localAddr));
                    Console.WriteLine($"      + {localAddr} 已加入 {multicastAddr}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      - {localAddr} 加入失败: {ex.Message}");
                }
            }

            if (_localIPv4Addresses.Count == 0)
            {
                // 没有可用接口时回退到默认
                Console.WriteLine($"      + (默认接口) 已加入 {multicastAddr}");
                _udpClient.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(multicastAddr));
            }

            // 发送 announce 上线通知
            await SendAnnouncementAsync(ct);

            // 持续监听
            await ReceiveLoopAsync(_listenCts.Token);
        }
        catch (OperationCanceledException) when (_listenCts?.IsCancellationRequested == true)
        {
            // 正常取消
        }
        finally
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }
    }

    /// <summary>
    /// 发送组播 announce 消息，请求同网段其他设备响应。
    /// </summary>
    public async Task SendAnnouncementAsync(CancellationToken ct = default)
    {
        var msg = BuildMulticastMessage(announce: true);
        var json = JsonSerializer.Serialize(msg);
        var data = Encoding.UTF8.GetBytes(json);

        try
        {
            while (true) 
            {
                // 发送 3 次（间隔模拟 Dart 实现的 100ms, 500ms, 2000ms）
                var delays = new[] { 100, 500, 2000 };
                foreach (var delay in delays)
                {
                    await Task.Delay(delay, ct);
                    if (ct.IsCancellationRequested) break;

                    Console.WriteLine($"    [组播] 发送 announce ({delay}ms)");
                    await SendMulticastOnAllInterfacesAsync(data, ct);
                }
                await Task.Delay(1000, ct);
            }           
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常取消
        }
    }

    /// <summary>
    /// 手动向组播发送 announce（不带延迟，立即发送一次）。
    /// </summary>
    public async Task TriggerAnnouncementAsync(CancellationToken ct = default)
    {
        var msg = BuildMulticastMessage(announce: true);
        var json = JsonSerializer.Serialize(msg);
        var data = Encoding.UTF8.GetBytes(json);

        await SendMulticastOnAllInterfacesAsync(data, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(5000); // 5 秒超时，便于检查取消状态

                var result = await _udpClient.ReceiveAsync(cts.Token);
                if (ct.IsCancellationRequested) break;

                var remoteIp = result.RemoteEndPoint.Address;
                var json = Encoding.UTF8.GetString(result.Buffer, 0, result.Buffer.Length);

                Console.WriteLine($"    [组播] 收到 UDP 来自 {remoteIp}: {json}");

                try
                {
                    var msg = JsonSerializer.Deserialize<MulticastMessageV2>(json);
                    if (msg == null) continue;

                    // 过滤自身消息
                    if (msg.Fingerprint == _ownFingerprint)
                    {
                        Console.WriteLine($"    [组播]   → 自身消息，已过滤");
                        continue;
                    }

                    var peer = new MulticastPeerInfo
                    {
                        Alias = msg.Alias,
                        Version = msg.Version,
                        DeviceModel = msg.DeviceModel,
                        DeviceType = msg.DeviceType,
                        Fingerprint = msg.Fingerprint,
                        Port = msg.Port,
                        Protocol = msg.Protocol,
                        Download = msg.Download,
                        IP = remoteIp
                    };

                    _discovered.AddOrUpdate(
                        remoteIp.ToString(),
                        peer,
                        (_, __) => peer);

                    DeviceDiscovered?.Invoke(this, peer);

                    var isAnnounce = msg.Announce || msg.Announcement == true;
                    if (isAnnounce && _serverRunning)
                    {
                        _ = RespondToAnnouncementAsync(peer, ct);
                    }
                }
                catch (JsonException)
                {
                    Console.WriteLine($"    [组播]   → JSON 解析失败");
                }
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RespondToAnnouncementAsync(MulticastPeerInfo peer, CancellationToken ct)
    {
        // 优先用 TCP 响应（POST /api/localsend/v2/register）
        // 使用对端声明的协议（http/https）连接对端，而非本机协议
        if (_httpClient != null)
        {
            try
            {
                var payload = new RegisterDtoV2
                {
                    Alias = _localInfo.Alias,
                    Version = ProtocolVersion,
                    DeviceType = _localInfo.DeviceType,
                    Fingerprint = _ownFingerprint,
                    Port = _httpPort,
                    Protocol = _protocol,       // 告诉对端如何连回本机
                    Download = false
                };

                var ipStr = peer.IP.ToString();
                // 使用对端的协议连接
                var peerProtocol = peer.Protocol == ProtocolTypeV2.Https ? ProtocolType.Https : ProtocolType.Http;
                await _httpClient.RegisterAsync(
                    protocol: peerProtocol,
                    ip: ipStr,
                    port: peer.Port,
                    payload: payload,
                    ct: ct);
                Console.WriteLine($"    [组播] TCP 响应 {peer.Alias} ({peer.IP}) 成功");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [组播] TCP 响应 {peer.Alias} ({peer.IP}) 失败: {ex.Message} → 回退 UDP");
            }
        }

        // UDP 回退：在所有接口上发送 announce=false 响应
        try
        {
            var msg = BuildMulticastMessage(announce: false);
            var json = JsonSerializer.Serialize(msg);
            var data = Encoding.UTF8.GetBytes(json);

            await SendMulticastOnAllInterfacesAsync(data, ct);
        }
        catch
        {
            // 忽略 UDP 响应失败
        }
    }

    private MulticastMessageV2 BuildMulticastMessage(bool announce)
    {
        return new MulticastMessageV2
        {
            Alias = _localInfo.Alias,
            Version = ProtocolVersion,
            DeviceModel = null,
            DeviceType = _localInfo.DeviceType,
            Fingerprint = _ownFingerprint,
            Port = _httpPort,
            Protocol = _protocol,
            Download = false,
            Announce = announce,
            Announcement = announce
        };
    }

    public void Dispose()
    {
        _listenCts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _listenCts?.Dispose();
        _discovered.Clear();
    }
}

/// <summary>
/// 通过 UDP 组播发现的对端设备信息。
/// </summary>
public sealed class MulticastPeerInfo
{
    public string Alias { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? DeviceModel { get; init; }
    public DeviceType? DeviceType { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public ushort Port { get; init; }
    public ProtocolTypeV2 Protocol { get; init; }
    public bool Download { get; init; }
    public IPAddress IP { get; init; } = IPAddress.None;

    public override string ToString() =>
        $"[{Alias}] {IP}:{Port} (v{Version}, {Protocol})";
}
