using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;
using FileShare.Desktop.Helpers;
using FileShare.Desktop.Services;
using System.Reflection;

namespace FileShare.Desktop.Tests.ViewModels;

/// <summary>
/// 网段扫描进度节流器的语义：重点是「被丢弃的事件不进入 UI 队列」（dispatch 调用次数），
/// 以及发现设备 / 收尾进度两类事件强制放行。
/// </summary>
public class SubnetScanThrottleTests
{
    private static (IProgress<SubnetScanProgress> Progress, Func<int> DispatchCount) Create()
    {
        var applied = new List<SubnetScanProgress>();
        var dispatched = 0;

        Action<SubnetScanProgress> apply = item => applied.Add(item);
        Action<Action> dispatch = action =>
        {
            Interlocked.Increment(ref dispatched);
            action(); // 测试里同步执行，避免依赖 UI 线程
        };

        var progress = new ThrottledScanProgress(apply, dispatch);
        return (progress, () => dispatched);
    }

    private static SubnetScanProgress Plain(int scanned, int total = 100) =>
        new(scanned, total, "192.168.1." + scanned, null);

    private static SubnetScanProgress WithDevice(int scanned, int total = 100) =>
        new(scanned, total, "192.168.1." + scanned,
            new DeviceInfo
            {
                DeviceId = "found-" + scanned,
                DeviceName = "Found",
                IpAddress = "192.168.1." + scanned
            });

    [Fact]
    public void RapidPlainReports_AreThrottledBeforeReachingUiQueue()
    {
        var (progress, dispatchCount) = Create();

        for (var i = 1; i <= 50; i++)
        {
            progress.Report(Plain(i));
        }

        Assert.Equal(1, dispatchCount());
    }

    [Fact]
    public void FoundDevice_AlwaysPassesThrough()
    {
        var (progress, dispatchCount) = Create();

        progress.Report(Plain(1));       // 建立节流窗口
        progress.Report(WithDevice(2));  // 发现设备，不能被吞
        progress.Report(WithDevice(3));

        Assert.Equal(3, dispatchCount());
    }

    [Fact]
    public void FinalAddress_AlwaysPassesThrough()
    {
        var (progress, dispatchCount) = Create();

        progress.Report(Plain(1, total: 2));
        progress.Report(new SubnetScanProgress(2, 2, "192.168.1.2", null));

        Assert.Equal(2, dispatchCount());
    }

    [Fact]
    public void Report_AfterInterval_IsDispatchedAgain()
    {
        var (progress, dispatchCount) = Create();

        progress.Report(Plain(1));
        Thread.Sleep(160); // 超过 100ms 窗口
        progress.Report(Plain(2));

        Assert.Equal(2, dispatchCount());
    }
}
