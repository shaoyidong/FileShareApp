using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;
using FileShare.Desktop.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FileShare.Desktop.Tests.ViewModels;

/// <summary>
/// 网段扫描进度采集器的语义：Report 只做无锁聚合（不做任何 UI 投递），
/// Drain 取快照并完整保留期间发现的设备；并发上报不得丢失设备、进度不得回退。
/// UI 的刷新节奏由 ViewModel 的定时器负责，不在此测试范围。
/// </summary>
public class SubnetScanProgressCollectorTests
{
    private static DeviceInfo Device(int i) => new()
    {
        DeviceId = "found-" + i,
        DeviceName = "Found",
        IpAddress = "192.168.1." + i
    };

    private static SubnetScanProgress Plain(int scanned, int total = 100) =>
        new(scanned, total, "192.168.1." + scanned, null);

    [Fact]
    public void Drain_BeforeAnyReport_ReturnsZeroTotalAndNoDevices()
    {
        // ViewModel 依赖 Total<=0 判断「尚未收到上报」，此时不应刷新进度条
        var collector = new SubnetScanProgressCollector();

        var (latest, devices) = collector.Drain();

        Assert.Equal(0, latest.Total);
        Assert.Equal(0, latest.Scanned);
        Assert.Null(latest.CurrentAddress);
        Assert.Empty(devices);
    }

    [Fact]
    public void Drain_ReturnsLatestProgressAndFoundDevices()
    {
        var collector = new SubnetScanProgressCollector();
        collector.Report(Plain(1, total: 200));
        collector.Report(new SubnetScanProgress(2, 200, "192.168.1.2", Device(2)));
        collector.Report(Plain(3, total: 200)); // 之后又有普通进度，地址/计数应取最新

        var (latest, devices) = collector.Drain();

        Assert.Equal(200, latest.Total);
        Assert.Equal(3, latest.Scanned);
        Assert.Equal("192.168.1.3", latest.CurrentAddress);

        var device = Assert.Single(devices);
        Assert.Equal("found-2", device.DeviceId);
    }

    [Fact]
    public void Drain_ClearsDeviceQueue_NextDrainSeesOnlyNewDevices()
    {
        var collector = new SubnetScanProgressCollector();

        collector.Report(new SubnetScanProgress(1, 100, "192.168.1.1", Device(1)));
        var (_, first) = collector.Drain();
        Assert.Single(first);

        collector.Report(new SubnetScanProgress(2, 100, "192.168.1.2", Device(2)));
        var (_, second) = collector.Drain();

        var device = Assert.Single(second);
        Assert.Equal("found-2", device.DeviceId);
    }

    [Fact]
    public void Scanned_NeverGoesBackwards_OnOutOfOrderReports()
    {
        // 并发完成时上报到达顺序与计数器大小无关：旧值不得覆盖新值
        var collector = new SubnetScanProgressCollector();
        collector.Report(Plain(5, total: 10));
        collector.Report(Plain(3, total: 10));
        collector.Report(Plain(9, total: 10));
        collector.Report(Plain(2, total: 10));

        var (latest, _) = collector.Drain();

        Assert.Equal(9, latest.Scanned);
    }

    [Fact]
    public async Task ConcurrentReports_LoseNoDevices_AndFinalScannedIsMax()
    {
        const int threads = 8;
        const int reportsPerThread = 500;
        const int totalReports = threads * reportsPerThread;

        // 打乱顺序，制造多线程乱序上报（真实扫描是 64 并发探测、完成顺序随机）
        var values = Enumerable.Range(0, totalReports).ToArray();
        var random = new Random(20261112);
        for (var i = values.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }

        var collector = new SubnetScanProgressCollector();
        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            // 每个线程只处理自己的分片：打乱后按线程切分，模拟 64 并发探测的乱序完成
            foreach (var v in values.AsSpan(t * reportsPerThread, reportsPerThread))
            {
                DeviceInfo? device = v % 7 == 0 ? Device(v) : null;
                collector.Report(new SubnetScanProgress(v + 1, totalReports, "192.168.1." + v, device));
            }
        })));

        var (latest, devices) = collector.Drain();

        Assert.Equal(totalReports, latest.Scanned);
        Assert.Equal(totalReports, latest.Total);
        Assert.Equal(values.Count(v => v % 7 == 0), devices.Length);
    }

    [Fact]
    public void ReadOnlySnapshots_ExposeScannedAndCurrentAddress_ForLowFrequencyIpDisplay()
    {
        var collector = new SubnetScanProgressCollector();
        Assert.Equal(0, collector.Scanned);
        Assert.Null(collector.CurrentAddress);
        Assert.Equal(0, collector.Total);

        collector.Report(new SubnetScanProgress(7, 254, "192.168.1.7", null));

        Assert.Equal(7, collector.Scanned);
        Assert.Equal("192.168.1.7", collector.CurrentAddress);
        Assert.Equal(254, collector.Total);
        Assert.True(Math.Abs(collector.Percent - 2.8) < 0.05);

        // 快照属性只读：不应清空设备队列或影响后续 Drain
        collector.Report(new SubnetScanProgress(8, 254, "192.168.1.8", Device(8)));
        var (latest, devices) = collector.Drain();

        Assert.Equal(8, latest.Scanned);
        Assert.Equal("192.168.1.8", latest.CurrentAddress);
        var device = Assert.Single(devices);
        Assert.Equal("found-8", device.DeviceId);
    }
}
