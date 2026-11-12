using FileShare.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FileShare.Core.Network.Discovery;

// SubnetScanProgress / ISubnetScanner 已拆到 ISubnetScanner.cs。

/// <inheritdoc cref="ISubnetScanner"/>
public sealed class SubnetScanner : ISubnetScanner
{
    /// <summary>并发探测数。早期用 64，实测在 Windows 上并发创建/销毁 UdpClient 与
    /// 定时器会和窗口拖动、输入争用系统资源；降到 32 后扫描总时长不变（单地址超时同步缩短），
    /// 但后台同时活跃的 socket / CTS / 定时器数量减半。</summary>
    public const int DefaultConcurrency = 64;

    /// <summary>单次扫描的地址上限（/20 = 4096 个地址），防止误输入 /8 造成全网风暴。</summary>
    public const int MaxHostsPerScan = 4096;

    /// <summary>单个地址探测的总上限：局域网内 FileShare 设备收到 UDP 发现报文后通常在
    /// 50ms 内应答，500ms 足够；不可达地址不再等满 UDP 700ms + TCP 800ms。
    /// 与 32 并发配合，/24 总时长与旧版 64 并发/1.5s 基本持平。</summary>
    private const int ScanProbeTimeoutMs = 500;

    private readonly IDeviceProbeService _probeService;
    private readonly ILogger<SubnetScanner> _logger;

    public SubnetScanner(IDeviceProbeService probeService, ILoggerFactory? loggerFactory = null)
    {
        _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));
        _logger = loggerFactory?.CreateLogger<SubnetScanner>() ?? NullLogger<SubnetScanner>.Instance;
    }

    /// <summary>
    /// 解析 CIDR。
    /// </summary>
    /// <param name="input">用户输入。</param>
    /// <param name="network">网络地址（未与掩码对齐，调用方可用 <see cref="EnumerateHosts"/> 规范化）。</param>
    /// <param name="prefixLength">前缀长度。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryParseCidr(string? input, out IPAddress? network, out int prefixLength, out string? error)
    {
        network = null;
        prefixLength = 0;
        error = null;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "请输入网段，例如 192.168.1.0/24";
            return false;
        }

        var parts = text.Split('/', 2);
        if (!IPAddress.TryParse(parts[0].Trim(), out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "网段必须使用 IPv4 地址，例如 192.168.1.0/24";
            return false;
        }

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1].Trim(), out prefixLength) || prefixLength is < 20 or > 32)
            {
                error = "前缀长度必须在 20~32 之间（单次最多扫描 4094 台主机）";
                return false;
            }
        }
        else
        {
            prefixLength = 24;
        }

        network = address;
        return true;
    }

    /// <summary>
    /// 枚举网段内需要探测的主机地址（跳过网络地址与广播地址，/31、/32 例外）。
    /// </summary>
    public static IReadOnlyList<IPAddress> EnumerateHosts(IPAddress network, int prefixLength)
    {
        var hosts = new List<IPAddress>();
        if (network is null || prefixLength is < 0 or > 32)
        {
            return hosts;
        }

        var bytes = network.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return hosts;
        }

        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var net = value & mask;
        var broadcast = net | ~mask;

        var first = prefixLength >= 31 ? net : net + 1;
        var last = prefixLength >= 31 ? broadcast : broadcast - 1;

        if (last < first)
        {
            return hosts;
        }

        var count = (long)last - first + 1;
        for (long i = 0; i < count && hosts.Count < MaxHostsPerScan; i++)
        {
            hosts.Add(FromUInt32((uint)(first + i)));
        }

        return hosts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceInfo>> ScanAsync(
        string cidr,
        IProgress<SubnetScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCidr(cidr, out var network, out var prefixLength, out var error) || network is null)
        {
            throw new ArgumentException(error ?? "无效的网段", nameof(cidr));
        }

        var hosts = EnumerateHosts(network, prefixLength);
        if (hosts.Count == 0)
        {
            return Array.Empty<DeviceInfo>();
        }

        var stopwatch = Stopwatch.StartNew();

        IReadOnlyList<DeviceInfo> found;
        if (_probeService is IBatchDeviceProbeService batch)
        {
            // 生产路径：共享 socket 的批量探测跑在单条低优先级专用线程上，
            // 内核对象 O(1)、零线程池占用、零定时器风暴。
            // 逐地址路径那套「每地址一个 UdpClient + CTS + TCP 兜底」会在系统层面
            // 与窗口拖动 / 输入争用资源，即使完全不向 UI 投递消息也会卡。
            found = await batch.ProbeBatchAsync(hosts, progress, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                // 用户主动停止：返回已发现的设备，不抛异常。批量探测的取消是正常控制流，
                // 由上层（ViewModel）根据令牌状态给出「已停止扫描」文案。
                _logger.LogInformation(
                    "网段 {Cidr} 扫描已停止：共 {Total} 个地址，发现 {Found} 台设备，耗时 {Elapsed}ms",
                    cidr, hosts.Count, found.Count, stopwatch.ElapsedMilliseconds);
                return found;
            }
        }
        else
        {
            // 回退路径：自定义探测服务 / 单元测试替身，保持逐地址语义完全不变。
            // 固定 DefaultConcurrency 个 worker，用原子索引动态领取地址。
            found = await Task.Run(async () =>
            {
                var foundList = new List<DeviceInfo>();
                var scanned = 0;
                var total = hosts.Count;
                var nextIndex = -1;
                var localAddresses = new HashSet<IPAddress>(LocalNetworkHelper.GetLocalIPv4Addresses());

                var workers = new Task[DefaultConcurrency];
                for (var w = 0; w < workers.Length; w++)
                {
                    workers[w] = Task.Run(async () =>
                    {
                        while (true)
                        {
                            var index = Interlocked.Increment(ref nextIndex);
                            if (index >= total)
                            {
                                return;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            var host = hosts[index];
                            DeviceInfo? device = null;
                            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            probeCts.CancelAfter(ScanProbeTimeoutMs);
                            try
                            {
                                if (!localAddresses.Contains(host))
                                {
                                    device = await _probeService
                                        .ProbeAsync(host, 0, probeCts.Token)
                                        .ConfigureAwait(false);

                                    if (device is not null)
                                    {
                                        lock (foundList)
                                        {
                                            if (!foundList.Any(item => item.DeviceId == device.DeviceId))
                                            {
                                                foundList.Add(device);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "扫描 {Host} 失败", host);
                            }
                            finally
                            {
                                var current = Interlocked.Increment(ref scanned);
                                progress?.Report(new SubnetScanProgress(current, total, host.ToString(), device));
                            }
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(workers).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return (IReadOnlyList<DeviceInfo>)foundList;
            }, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "网段 {Cidr} 扫描完成：共 {Total} 个地址，发现 {Found} 台设备，耗时 {Elapsed}ms",
            cidr,
            hosts.Count,
            found.Count,
            stopwatch.ElapsedMilliseconds);

        return found;
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }
}
