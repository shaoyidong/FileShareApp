using FileShare.Core.Common;
using FileShare.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// 单设备探测服务：向指定地址发一个发现报文，读取对端回包从而取得完整的设备信息。
/// <para>它解决两个在真实局域网里非常常见的问题：</para>
/// <list type="bullet">
/// <item>设备处于不同子网（广播/组播过不去），但三层路由可达 —— 直接给 IP 即可；</item>
/// <item>对端防火墙拦截了入站 UDP 广播，但服务端口仍然对 TCP 开放 —— 退化为 TCP 探活。</item>
/// </list>
/// <para>UDP 探测伪装成一次普通的发现问答（与 <c>UdpBroadcastDiscoveryService</c> 使用同一报文格式），
/// 因此对端无需任何改动即可应答；TCP 探活仅用于确认端口存活，此时拿不到设备身份，
/// 会返回一个以 IP 命名的占位设备，仍然可以直接发送文件。</para>
/// </summary>
public interface IDeviceProbeService
{
    /// <summary>
    /// 探测用户输入的地址（IP、IP:端口、主机名）。
    /// </summary>
    Task<DeviceInfo?> ProbeAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// 探测指定地址与端口。
    /// </summary>
    Task<DeviceInfo?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDeviceProbeService"/>
public sealed class DeviceProbeService : IDeviceProbeService
{
    private const string DiscoveryMessage = "FileShareDiscovery";
    private const int DefaultUdpDiscoveryPort = 5236;
    private const int DefaultUdpMulticastPort = 53317;
    private const int DefaultTransferPort = 5237;

    private static readonly int[] ProbeUdpPorts = { DefaultUdpDiscoveryPort, DefaultUdpMulticastPort };

    private readonly DeviceInfo _localDevice;
    private readonly int _tcpFallbackPort;
    private readonly ILogger<DeviceProbeService> _logger;
    private readonly TimeSpan _udpTimeout;

    /// <summary>
    /// 创建探测器。
    /// </summary>
    /// <param name="localDevice">本机设备信息，会随探测报文一起发出，使对端可以正常识别本机。</param>
    /// <param name="tcpFallbackPort">UDP 无应答时用于 TCP 探活的文件传输端口。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="udpTimeout">单个地址的 UDP 等待时间。</param>
    public DeviceProbeService(
        DeviceInfo localDevice,
        int tcpFallbackPort = DefaultTransferPort,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? udpTimeout = null)
    {
        _localDevice = localDevice ?? throw new ArgumentNullException(nameof(localDevice));
        _tcpFallbackPort = tcpFallbackPort > 0 ? tcpFallbackPort : DefaultTransferPort;
        _logger = loggerFactory?.CreateLogger<DeviceProbeService>() ?? NullLogger<DeviceProbeService>.Instance;
        _udpTimeout = udpTimeout ?? TimeSpan.FromMilliseconds(700);
    }

    /// <inheritdoc />
    public async Task<DeviceInfo?> ProbeAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!DeviceAddress.TryParse(address, _tcpFallbackPort, out var parsed, out var error))
        {
            _logger.LogDebug("地址解析失败: {Error}", error);
            return null;
        }

        var targets = await ResolveAsync(parsed.Host, cancellationToken).ConfigureAwait(false);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var device = await ProbeAsync(target, parsed.Port, cancellationToken).ConfigureAwait(false);
            if (device is not null)
            {
                return device;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<DeviceInfo?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        if (address is null)
        {
            return null;
        }

        if (LocalNetworkHelper.IsLocalAddress(address))
        {
            // 探测本机没有意义，还会让设备列表出现「自己」
            return null;
        }

        var byUdp = await ProbeByUdpAsync(address, cancellationToken).ConfigureAwait(false);
        if (byUdp is not null)
        {
            if (byUdp.Port <= 0)
            {
                byUdp.Port = port > 0 ? port : _tcpFallbackPort;
            }

            return byUdp;
        }

        return await ProbeByTcpAsync(address, port > 0 ? port : _tcpFallbackPort, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new[] { literal };
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// 发送发现报文并等待应答。报文格式与 UDP 广播发现一致，对端发现服务会自动回复。
    /// </summary>
    private async Task<DeviceInfo?> ProbeByUdpAsync(IPAddress address, CancellationToken cancellationToken)
    {
        UdpClient? udp = null;

        try
        {
            udp = address.AddressFamily == AddressFamily.InterNetworkV6
                ? new UdpClient(AddressFamily.InterNetworkV6)
                : new UdpClient(0);

            var payload = BuildProbePacket();

            foreach (var udpPort in ProbeUdpPorts)
            {
                try
                {
                    await udp.SendAsync(payload, payload.Length, new IPEndPoint(address, udpPort))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "向 {Address}:{Port} 发送探测报文失败", address, udpPort);
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_udpTimeout);

            while (true)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "接收 {Address} 的探测应答失败", address);
                    return null;
                }

                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(DiscoveryMessage, StringComparison.Ordinal))
                {
                    continue;
                }

                DeviceInfo? device;
                try
                {
                    device = JsonSerializer.Deserialize(
                        text[DiscoveryMessage.Length..],
                        SourceGenerationContext.Default.DeviceInfo);
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "解析 {Address} 的探测应答失败", address);
                    continue;
                }

                if (device is null || string.IsNullOrEmpty(device.DeviceId))
                {
                    continue;
                }

                if (device.DeviceId == _localDevice.DeviceId)
                {
                    return null;
                }

                // 以探测目标地址为准：对端多网卡时上报的地址未必可达
                device.IpAddress = address.ToString();
                device.LastSeen = DateTime.Now;
                return device;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UDP 探测 {Address} 失败", address);
            return null;
        }
        finally
        {
            udp?.Dispose();
        }
    }

    /// <summary>
    /// TCP 探活：只确认传输端口是否开放，拿不到设备身份，返回占位设备。
    /// </summary>
    private async Task<DeviceInfo?> ProbeByTcpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(800));

            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);

            return new DeviceInfo
            {
                DeviceId = $"manual:{address}-{port}",
                DeviceName = address.ToString(),
                IpAddress = address.ToString(),
                Port = port,
                DeviceType = DeviceType.Desktop,
                LastSeen = DateTime.Now,
                SupportsTls = false
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TCP 探活 {Address}:{Port} 失败", address, port);
            return null;
        }
    }

    private byte[] BuildProbePacket()
    {
        var json = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
        return Encoding.UTF8.GetBytes(DiscoveryMessage + json);
    }
}
