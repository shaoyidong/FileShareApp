using System.Net;
using FileShare.Core.Models;

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// 网段扫描专用的「批量探测」能力（可选实现，不改动 <see cref="IDeviceProbeService"/> 的契约）。
/// <para>
/// 逐地址探测会为每个地址新建一个 UdpClient、两三个 CancellationTokenSource/定时器，
/// 以及一次 TCP 兜底连接。扫描 /20（4094 个地址）时会在短时间内创建上万个内核对象，
/// 即使完全不向 UI 投递消息，也会在系统层面（socket/端口分配、定时器队列、线程池注入、GC）
/// 与窗口拖动、输入处理争用资源 —— 这是「UI 线程诊断正常但手动拖窗口仍卡」的根因。
/// </para>
/// <para>
/// 实现方用一个共享 socket 集中收发、跑在单条低优先级专用线程上，
/// 把内核对象与线程池占用从 O(地址数) 降到 O(1)。
/// <see cref="SubnetScanner"/> 仅在探测服务实现本接口时走这条快速路径，
/// 否则回退到逐地址路径，保证自定义实现与单元测试替身的行为完全不变。
/// </para>
/// </summary>
public interface IBatchDeviceProbeService
{
    /// <summary>
    /// 对整批地址做一次共享 socket 的 UDP 发现探测。
    /// 必须像逐地址路径一样对每个地址如实上报一次进度（含被跳过的本机地址），
    /// 否则换一个 UI 就会拿不到完整进度。
    /// </summary>
    /// <param name="addresses">待探测的地址。</param>
    /// <param name="progress">进度上报；可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DeviceInfo[]> ProbeBatchAsync(
        IReadOnlyList<IPAddress> addresses,
        IProgress<SubnetScanProgress>? progress,
        CancellationToken cancellationToken);
}
