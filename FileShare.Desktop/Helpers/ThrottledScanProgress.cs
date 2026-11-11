using FileShare.Core.Network.Discovery;
using System;
using System.Threading;

namespace FileShare.Desktop.Helpers;

/// <summary>
/// 网段扫描进度的 UI 节流器：进度事件可能以每秒几十上百条的频率到达，
/// 逐条投递会把 UI 线程压出可感知的卡顿。这里按时间窗丢弃普通进度，
/// 只放行两类高价值事件：发现了设备、扫描收尾（最后一条）。
/// </summary>
internal sealed class ThrottledScanProgress : IProgress<SubnetScanProgress>
{
    /// <summary>UI 刷新最小间隔（毫秒）。</summary>
    private const int IntervalMs = 100;

    private readonly Action<SubnetScanProgress> _apply;
    private readonly Action<Action> _dispatchToUi;
    private long _lastReportTicks;

    public ThrottledScanProgress(Action<SubnetScanProgress> apply, Action<Action> dispatchToUi)
    {
        _apply = apply;
        _dispatchToUi = dispatchToUi;
    }

    public void Report(SubnetScanProgress value)
    {
        var now = Environment.TickCount64;
        var mustReport = value.FoundDevice is not null || value.Scanned >= value.Total;
        if (!mustReport && now - Interlocked.Read(ref _lastReportTicks) < IntervalMs)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReportTicks, now);
        _dispatchToUi(() => _apply(value));
    }
}
