using FileShare.Core.Common;
using FileShare.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FileShare.Core.Network.Discovery;

// 接口定义已拆到 IDeviceProbeService.cs。

/// <inheritdoc cref="IDeviceProbeService"/>
public sealed class DeviceProbeService : IDeviceProbeService, IBatchDeviceProbeService
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
                catch
                {
                    // 对端不可达 / ICMP 重置是扫描的预期结果，逐条记日志会把 UI 线程的日志排到锁队尾
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
                catch (SocketException)
                {
                    // 对端 ICMP Port Unreachable / 连接重置是探测的预期结果，不记日志
                    return null;
                }

                var device = ParseProbeReply(result.Buffer, address);
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
            // 超时（无应答）是探活的预期结果；其余失败也不影响扫描结果，统一静默
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---- 网段扫描专用：共享 socket 批量探测 ----

    /// <summary>每批发送的地址数。拆批既避免瞬时 UDP 风暴，也让低频进度 / IP 显示有意义。</summary>
    private const int ProbeBatchSize = 128;
    /// <summary>每批的应答收集窗口（毫秒）。socket 不关闭，迟到的应答会在下一批被收到。</summary>
    private const int ProbeBatchWindowMs = 120;
    /// <summary>最后一批之后的收尾窗口（毫秒），给慢设备留出应答时间。</summary>
    private const int ProbeFinalWindowMs = 300;

    /// <inheritdoc />
    public Task<IReadOnlyList<DeviceInfo>> ProbeBatchAsync(
        IReadOnlyList<IPAddress> addresses,
        IProgress<SubnetScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        // 整批跑在一条低优先级专用线程上：全同步 socket 收发，不占用默认线程池，
        // 也不产生 async 续体排队。Windows 调度器因此始终把 UI 线程 / DWM 排在前面。
        return Task.Factory.StartNew(
            () => ProbeBatchCore(addresses, progress, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private IReadOnlyList<DeviceInfo> ProbeBatchCore(
        IReadOnlyList<IPAddress> addresses,
        IProgress<SubnetScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        // LongRunning 保证这里是独立线程（不是线程池线程），可以安全调低优先级。
        // 即便平台不支持，也只是退回普通优先级，不影响功能。
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch (PlatformNotSupportedException)
        {
            // 部分平台不允许修改线程优先级
        }

        var total = addresses.Count;
        if (total == 0)
        {
            return Array.Empty<DeviceInfo>();
        }

        var found = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);
        var repliesByIp = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);
        var localAddresses = new HashSet<IPAddress>(LocalNetworkHelper.GetLocalIPv4Addresses());
        var payload = BuildProbePacket();
        var scanned = 0;
        var batches = 0;

        using var udp = new UdpClient(0);
        try
        {
            udp.Client.ReceiveBufferSize = 512 * 1024;

            // Windows 会把「ICMP port unreachable」转成 ConnectionReset 异常。
            // 扫描大量不可达地址时会反复打断接收循环，这里显式关掉该行为。
            if (OperatingSystem.IsWindows())
            {
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
        }
        catch (SocketException)
        {
            // 平台不支持该选项时忽略，下面的 catch 仍能兜住 ICMP 异常
        }

        for (var start = 0; start < total; start += ProbeBatchSize)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            batches++;
            var end = Math.Min(start + ProbeBatchSize, total);

            // 同步 sendto：没有 async 状态机、没有 Task 分配；单播 UDP 只写本地缓冲，不阻塞。
            for (var i = start; i < end; i++)
            {
                var address = addresses[i];
                if (address.AddressFamily != AddressFamily.InterNetwork || localAddresses.Contains(address))
                {
                    continue;
                }

                foreach (var port in ProbeUdpPorts)
                {
                    try
                    {
                        udp.Send(payload, payload.Length, new IPEndPoint(address, port));
                    }
                    catch (SocketException)
                    {
                        // 对端不可达 / ICMP 重置是扫描的预期结果
                    }
                }
            }

            ReceiveWindow(udp, repliesByIp, found, ProbeBatchWindowMs);

            for (var i = start; i < end; i++)
            {
                var key = addresses[i].ToString();
                repliesByIp.TryGetValue(key, out var device);
                progress?.Report(new SubnetScanProgress(++scanned, total, key, device));
            }
        }

        if (batches > 0 && !cancellationToken.IsCancellationRequested)
        {
            ReceiveWindow(udp, repliesByIp, found, ProbeFinalWindowMs);
        }

        // 用户主动停止是正常控制流：后台专用线程不抛异常，直接返回已发现的设备。
        // 取消语义由上层根据令牌状态表达，避免 OCE 从 LongRunning 线程冒泡成
        // Canceled / Faulted Task，进而变成未观察异常或 first-chance exception。
        return found.Values.ToArray();
    }

    /// <summary>
    /// 在一个时间窗口内集中收集应答。总时长由 Stopwatch 封顶，避免持续有应答时窗口被无限延长。
    /// socket 全程保持打开，上一批迟到的应答会在下一批或收尾窗口被收到。
    /// </summary>
    private void ReceiveWindow(
        UdpClient udp,
        Dictionary<string, DeviceInfo> repliesByIp,
        Dictionary<string, DeviceInfo> found,
        int windowMs)
    {
        udp.Client.ReceiveTimeout = windowMs;
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < windowMs)
        {
            IPEndPoint? remote = new(IPAddress.Any, 0);
            byte[] buffer;
            try
            {
                buffer = udp.Receive(ref remote);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                break;
            }
            catch (SocketException)
            {
                // 单个 ICMP / 畸形包不能吞掉整批应答
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var source = remote?.Address ?? IPAddress.None;
            var device = ParseProbeReply(buffer, source);
            if (device is null || string.IsNullOrEmpty(device.DeviceId) ||
                device.DeviceId == _localDevice.DeviceId)
            {
                continue;
            }

            // 以实际回包源地址为准：批量探测没有「目标地址」上下文，
            // 而这个地址一定是刚刚能到达本机的，比探测目标更可靠。
            device.IpAddress = source.ToString();
            device.LastSeen = DateTime.Now;
            if (device.Port <= 0)
            {
                device.Port = _tcpFallbackPort;
            }

            repliesByIp[device.IpAddress] = device;
            found.TryAdd(device.DeviceId, device);
        }
    }

    /// <summary>解析 UDP 发现应答；报文无效或畸形时返回 null。</summary>
    private DeviceInfo? ParseProbeReply(byte[] buffer, IPAddress source)
    {
        var text = Encoding.UTF8.GetString(buffer);
        if (!text.StartsWith(DiscoveryMessage, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                text[DiscoveryMessage.Length..],
                SourceGenerationContext.Default.DeviceInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "解析 {Address} 的探测应答失败", source);
            return null;
        }
    }

    private byte[] BuildProbePacket()
    {
        var json = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
        return Encoding.UTF8.GetBytes(DiscoveryMessage + json);
    }
}
