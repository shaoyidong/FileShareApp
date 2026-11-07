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
    /// <summary>并发探测数：兼顾速度与路由器 ARP 表压力。</summary>
    public const int DefaultConcurrency = 64;

    /// <summary>单次扫描的地址上限（/20 = 4096 个地址），防止误输入 /8 造成全网风暴。</summary>
    public const int MaxHostsPerScan = 4096;

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

        var found = new List<DeviceInfo>();
        var scanned = 0;
        var total = hosts.Count;
        var localAddresses = LocalNetworkHelper.GetLocalIPv4Addresses();
        using var gate = new SemaphoreSlim(DefaultConcurrency, DefaultConcurrency);
        var stopwatch = Stopwatch.StartNew();

        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            DeviceInfo? device = null;
            try
            {
                if (!cancellationToken.IsCancellationRequested && !localAddresses.Contains(host))
                {
                    device = await _probeService
                        .ProbeAsync(host, 0, cancellationToken)
                        .ConfigureAwait(false);

                    if (device is not null)
                    {
                        lock (found)
                        {
                            if (!found.Any(item => item.DeviceId == device.DeviceId))
                            {
                                found.Add(device);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消：忽略剩余地址
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "扫描 {Host} 失败", host);
            }
            finally
            {
                var current = Interlocked.Increment(ref scanned);
                progress?.Report(new SubnetScanProgress(current, total, host.ToString(), device));
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation(
            "网段 {Cidr} 扫描完成：共 {Total} 个地址，发现 {Found} 台设备，耗时 {Elapsed}ms",
            cidr,
            total,
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
