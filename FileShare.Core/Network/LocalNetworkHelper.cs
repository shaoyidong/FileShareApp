using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FileShare.Core.Network;

/// <summary>
/// 本地网卡地址工具：用于跳过本机地址、推荐扫描网段、以及多网卡环境下的地址选择。
/// </summary>
public static class LocalNetworkHelper
{
    // GetAllNetworkInterfaces 在 Windows 上是同步且内部串行化的重调用（实测单次约 124ms）。
    // 网段扫描 64 并发下每 3 秒跨一次缓存过期，若让每个线程各自枚举网卡，实测要 3.6 秒，
    // 会把线程池冻结数秒——表现为扫描时停止按钮点不动、拖窗口卡死。
    // 因此：调用方永远只读缓存（旧值也可用），刷新只允许一个后台任务执行（stale-while-revalidate）。
    private static readonly Lock CacheLock = new();
    private static IReadOnlyList<(IPAddress Address, IPAddress Mask)>? _cachedInterfaces;
    private static IReadOnlyList<IPAddress> _cachedLocalAddresses = Array.Empty<IPAddress>();
    private static long _cachedAtTicks;
    private static int _refreshRunning;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 枚举所有「已启用、非回环、非隧道」网卡上的 IPv4 地址与子网掩码。
    /// <para>缓存存在时直接返回；过期时先返回旧值，再由单个后台任务刷新，
    /// 调用方永远不会被同步网卡枚举阻塞（首次调用除外）。</para>
    /// </summary>
    public static IReadOnlyList<(IPAddress Address, IPAddress Mask)> GetIPv4Interfaces()
    {
        IReadOnlyList<(IPAddress Address, IPAddress Mask)>? cached;
        long stamp;
        lock (CacheLock)
        {
            cached = _cachedInterfaces;
            stamp = _cachedAtTicks;
        }

        if (cached is null)
        {
            return RefreshNow();
        }

        if (Environment.TickCount64 - stamp > CacheLifetime.TotalMilliseconds)
        {
            StartBackgroundRefresh();
        }

        return cached;
    }

    /// <summary>首次调用（或缓存被显式清空后）同步取一次值；此后一律由后台刷新。</summary>
    private static IReadOnlyList<(IPAddress Address, IPAddress Mask)> RefreshNow()
    {
        lock (CacheLock)
        {
            if (_cachedInterfaces is not null)
            {
                return _cachedInterfaces;
            }

            Publish(EnumerateIPv4Interfaces());
            return _cachedInterfaces!;
        }
    }

    /// <summary>只起一个刷新任务：并发线程同时发现缓存过期时不会一起进入同步枚举。</summary>
    private static void StartBackgroundRefresh()
    {
        if (Interlocked.Exchange(ref _refreshRunning, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var fresh = EnumerateIPv4Interfaces();
                lock (CacheLock)
                {
                    Publish(fresh);
                }
            }
            catch
            {
                // 刷新失败保留旧值；后台任务异常必须吞掉，避免变成 Unobserved
            }
            finally
            {
                Volatile.Write(ref _refreshRunning, 0);
            }
        });
    }

    /// <summary>发布新快照，调用方必须持有 <see cref="CacheLock"/>。</summary>
    private static void Publish(IReadOnlyList<(IPAddress Address, IPAddress Mask)> fresh)
    {
        // 缓存是共享实例，对外只读，避免调用方误改污染后续所有读取
        var snapshot = fresh.ToList().AsReadOnly();
        _cachedInterfaces = snapshot;
        _cachedLocalAddresses = Array.AsReadOnly(snapshot.Select(item => item.Address).Distinct().ToArray());
        _cachedAtTicks = Environment.TickCount64;
    }

    /// <summary>强制刷新缓存：保留旧值并立即触发一次后台刷新（用于感知网卡变化）。</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedAtTicks = 0;
        }

        StartBackgroundRefresh();
    }

    private static IReadOnlyList<(IPAddress Address, IPAddress Mask)> EnumerateIPv4Interfaces()
    {
        var resultRank = new List<(IPAddress Address, IPAddress Mask, int rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                // 有网关的网卡更可能是真实的、可达外网的物理网卡，优先选择
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                int rank = hasGateway ? 0 : 1;

                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var mask = unicast.IPv4Mask;
                    if (mask is null)
                    {
                        continue;
                    }

                    resultRank.Add((unicast.Address, mask, rank));
                }
            }
        }
        catch
        {
            // 某些受限平台（如部分 iOS/Android 版本）会抛异常，调用方按「无网卡信息」处理
        }

        return resultRank
            .OrderBy(item => item.rank)
            .Select(item => (item.Address, item.Mask))
            .ToList();
    }

    /// <summary>本机所有 IPv4 地址（复用缓存，避免扫描时每个地址都重新做 LINQ 分配）。</summary>
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        GetIPv4Interfaces(); // 保证已初始化，并按需触发后台刷新
        lock (CacheLock)
        {
            return _cachedLocalAddresses;
        }
    }

    /// <summary>判断地址是否属于本机（用于扫描/探测时跳过自身）。</summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var local in GetLocalIPv4Addresses())
        {
            if (local.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 根据本机网卡推荐一个扫描网段，例如 <c>192.168.1.0/24</c>。
    /// 优先选择带网关的物理网卡。
    /// </summary>
    public static string? GetSuggestedCidr()
    {
        try
        {
            string? fallback = null;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork);

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    var mask = unicast.IPv4Mask;
                    if (mask is null)
                    {
                        continue;
                    }

                    var cidr = ToCidr(unicast.Address, mask);
                    if (cidr is null)
                    {
                        continue;
                    }

                    if (hasGateway)
                    {
                        return cidr;
                    }

                    fallback ??= cidr;
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把「地址 + 掩码」换算成 CIDR 形式。</summary>
    public static string? ToCidr(IPAddress address, IPAddress mask)
    {
        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return null;
        }

        var prefix = 0;
        foreach (var b in maskBytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0)
                {
                    prefix++;
                }
                else
                {
                    bit = -1;
                }
            }
        }

        // 前缀过小意味着要扫描的地址太多，这里直接取 /24 作为保守推荐
        if (prefix is < 20 or > 32)
        {
            prefix = 24;
        }

        var network = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            network[i] = (byte)(addressBytes[i] & maskBytes[i]);
        }

        return $"{new IPAddress(network)}/{prefix}";
    }
}
