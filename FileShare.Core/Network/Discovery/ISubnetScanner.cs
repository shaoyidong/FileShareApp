using FileShare.Core.Models;

namespace FileShare.Core.Network.Discovery;

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
