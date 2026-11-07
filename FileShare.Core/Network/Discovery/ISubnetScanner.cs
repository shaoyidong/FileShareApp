using FileShare.Core.Models;

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// 网段扫描进度。每次报告一个地址的探测结果。
/// </summary>
/// <param name="Scanned">已扫描地址数。</param>
/// <param name="Total">本次扫描的地址总数。</param>
/// <param name="CurrentAddress">当前地址。</param>
/// <param name="FoundDevice">该地址发现的设备（未发现时为 <c>null</c>）。</param>
public sealed record SubnetScanProgress(int Scanned, int Total, string? CurrentAddress, DeviceInfo? FoundDevice)
{
    /// <summary>完成百分比（0~100）。</summary>
    public double Percent => Total <= 0 ? 100d : Math.Round((double)Scanned / Total * 100d, 1);
}

/// <summary>
/// 网段扫描器：并发探测一个子网内的所有地址。
/// <para>用途：绝大多数局域网发现依赖广播/组播，一旦跨越子网（VLAN、VPN、路由器隔离），
/// 或者对端/本端防火墙拦截了广播，自动发现就会失效。此时让用户输入网段扫描（或直接输 IP），
/// 是 LocalSend 等工具的标准兜底方案。</para>
/// </summary>
public interface ISubnetScanner
{
    /// <summary>
    /// 扫描网段。
    /// </summary>
    /// <param name="cidr">CIDR 写法，如 <c>192.168.1.0/24</c>；也可只写 <c>192.168.1.0</c>（按 /24 处理）。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<DeviceInfo>> ScanAsync(
        string cidr,
        IProgress<SubnetScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
