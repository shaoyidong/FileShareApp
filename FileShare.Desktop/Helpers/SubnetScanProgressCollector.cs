using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace FileShare.Desktop.Helpers;

/// <summary>
/// 网段扫描进度的「采集器」：服务层仍按 <see cref="IProgress{SubnetScanProgress}"/> 如实上报，
/// 这里只做无锁状态聚合（Interlocked / ConcurrentQueue），不做任何 UI 投递。
/// <para>
/// UI 刷新节奏由 ViewModel 的定时器驱动：定时器周期性调 <see cref="Drain"/> 取快照，
/// 把两次刷新之间的所有上报压缩成一次 UI 更新。服务端的事件风暴因此与 UI 队列彻底解耦：
/// Report 路径上没有闭包分配、没有 Dispatcher 投递，扫描收尾 / 取消时的上报洪峰
/// 只会让下一次快照多几个设备，不会压垮渲染。
/// （取代旧的 ThrottledScanProgress：其 Read/Exchange 非原子配对，wave 边界会被
/// 并发线程同时穿透节流窗口，扫描收尾一秒内漏放十几个事件。）
/// </para>
/// </summary>
internal sealed class SubnetScanProgressCollector : IProgress<SubnetScanProgress>
{
    private int _scanned;
    private int _total;
    private object? _currentAddress; // 实际类型 string，Interlocked 原子换入换出
    private readonly ConcurrentQueue<DeviceInfo> _foundDevices = new();

    public void Report(SubnetScanProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // scanned 必须单调不回退：并发完成时上报到达顺序与计数器大小无关，CAS 抬到最大值
        BumpToMax(ref _scanned, value.Scanned);
        Volatile.Write(ref _total, value.Total);
        Interlocked.Exchange(ref _currentAddress, value.CurrentAddress);

        if (value.FoundDevice is not null)
        {
            _foundDevices.Enqueue(value.FoundDevice);
        }
    }

    /// <summary>当前完成百分比（0~100）。只读快照，不清空任何状态，
    /// 供 UI 线程的低优先级定时器直接读取，避免跨线程 Post。</summary>
    public double Percent
    {
        get
        {
            var total = Volatile.Read(ref _total);
            var scanned = Volatile.Read(ref _scanned);
            return total <= 0 ? 0d : Math.Round((double)scanned / total * 100d, 1);
        }
    }

    /// <summary>本次扫描的地址总数（0 表示尚未收到任何上报）。</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>已扫描地址数（只读快照，供低频状态文本显示）。</summary>
    public int Scanned => Volatile.Read(ref _scanned);

    /// <summary>最近上报的地址（只读快照，供低频状态文本显示）。</summary>
    public string? CurrentAddress => Volatile.Read(ref _currentAddress) as string;

    /// <summary>
    /// 取一次快照并清空已发现设备队列。
    /// <see cref="SubnetScanProgress.Total"/> 为 0 表示尚未收到任何上报（进度条此时不应刷新）。
    /// </summary>
    public (SubnetScanProgress Latest, DeviceInfo[] FoundDevices) Drain()
    {
        var devices = new List<DeviceInfo>();
        while (_foundDevices.TryDequeue(out var device))
        {
            devices.Add(device);
        }

        var latest = new SubnetScanProgress(
            Volatile.Read(ref _scanned),
            Volatile.Read(ref _total),
            Volatile.Read(ref _currentAddress) as string,
            null);
        return (latest, devices.ToArray());
    }

    /// <summary>把字段抬升到 value（只升不降），并发下用 CAS 防止旧值覆盖新值。</summary>
    private static void BumpToMax(ref int field, int value)
    {
        var spinner = new SpinWait();
        while (true)
        {
            var current = Volatile.Read(ref field);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref field, value, current) == current)
            {
                return;
            }

            spinner.SpinOnce();
        }
    }
}
