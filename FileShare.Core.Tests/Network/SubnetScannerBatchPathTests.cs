using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 扫描快速路径（共享 socket 批量探测）的契约：
/// 探测服务实现 <see cref="IBatchDeviceProbeService"/> 时走批量路径，
/// 仍逐地址如实上报进度、返回设备、可取消；未实现时保持原逐地址语义。
/// </summary>
public class SubnetScannerBatchPathTests
{
    private sealed class SyncProgress : IProgress<SubnetScanProgress>
    {
        public List<SubnetScanProgress> Items { get; } = new();

        public void Report(SubnetScanProgress value)
        {
            lock (Items)
            {
                Items.Add(value);
            }
        }
    }

    /// <summary>同时实现两个接口，用来验证 SubnetScanner 确实选择了批量路径。</summary>
    private sealed class FakeBatchProbe : IDeviceProbeService, IBatchDeviceProbeService
    {
        private readonly IReadOnlyList<DeviceInfo> _devices;

        public FakeBatchProbe(params DeviceInfo[] devices) => _devices = devices;

        public int BatchCalls;
        public int PerAddressCalls;

        public Task<DeviceInfo?> ProbeAsync(string address, CancellationToken cancellationToken = default)
            => Task.FromResult<DeviceInfo?>(null);

        public Task<DeviceInfo?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PerAddressCalls);
            return Task.FromResult<DeviceInfo?>(null);
        }

        public Task<IReadOnlyList<DeviceInfo>> ProbeBatchAsync(
            IReadOnlyList<IPAddress> addresses,
            IProgress<SubnetScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref BatchCalls);

            for (var i = 0; i < addresses.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SubnetScanProgress(
                    i + 1, addresses.Count, addresses[i].ToString(), null));
            }

            return Task.FromResult(_devices);
        }
    }

    private static DeviceInfo Peer(int i = 1) => new()
    {
        DeviceId = "peer-" + i,
        DeviceName = "Peer " + i,
        IpAddress = "192.168.1." + i,
        Port = 5237,
        DeviceType = DeviceType.Desktop
    };

    private static DeviceProbeService CreateRealProbe() => new(Peer(200));

    [Fact]
    public async Task BatchCapableProbe_UsesBatchPath_AndReportsEveryAddress()
    {
        var probe = new FakeBatchProbe();
        var scanner = new SubnetScanner(probe);
        var progress = new SyncProgress();

        await scanner.ScanAsync("203.0.113.0/29", progress, TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.BatchCalls);
        Assert.Equal(0, probe.PerAddressCalls);
        Assert.Equal(6, progress.Items.Count);
        Assert.Equal(100, progress.Items.Max(item => item.Percent));
        Assert.All(progress.Items, item => Assert.False(string.IsNullOrEmpty(item.CurrentAddress)));
    }

    [Fact]
    public async Task BatchPath_ReturnsDevicesFromProbe()
    {
        var expected = Peer(7);
        var scanner = new SubnetScanner(new FakeBatchProbe(expected));

        var devices = await scanner.ScanAsync("203.0.113.0/30", null, TestContext.Current.CancellationToken);

        var device = Assert.Single(devices);
        Assert.Equal("peer-7", device.DeviceId);
    }

    [Fact]
    public async Task BatchPath_PropagatesCancellation()
    {
        var scanner = new SubnetScanner(new FakeBatchProbe());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync("203.0.113.0/29", null, cts.Token));
    }

    [Fact]
    public async Task ProbeWithoutBatchInterface_KeepsLegacyPerAddressPath()
    {
        // 现有 SubnetScannerTests 的替身只实现 IDeviceProbeService，必须仍走原路径
        var probe = new LegacyOnlyProbe();
        var scanner = new SubnetScanner(probe);
        var progress = new SyncProgress();

        await scanner.ScanAsync("203.0.113.0/29", progress, TestContext.Current.CancellationToken);

        Assert.Equal(6, probe.PerAddressCalls);
        Assert.Equal(6, progress.Items.Count);
    }

    private sealed class LegacyOnlyProbe : IDeviceProbeService
    {
        public int PerAddressCalls;

        public Task<DeviceInfo?> ProbeAsync(string address, CancellationToken cancellationToken = default)
            => Task.FromResult<DeviceInfo?>(null);

        public Task<DeviceInfo?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PerAddressCalls);
            return Task.FromResult<DeviceInfo?>(null);
        }
    }

    [Fact]
    public void ParseProbeReply_AcceptsDiscoveryPacket_RejectsMalformed()
    {
        var method = typeof(DeviceProbeService).GetMethod(
            "ParseProbeReply", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var probe = CreateRealProbe();
        var payload = Encoding.UTF8.GetBytes("FileShareDiscovery" + JsonSerializer.Serialize(Peer(9)));

        var parsed = method!.Invoke(probe, new object[] { payload, IPAddress.Parse("192.168.1.9") }) as DeviceInfo;

        Assert.NotNull(parsed);
        Assert.Equal("peer-9", parsed!.DeviceId);
        Assert.Null(method.Invoke(probe,
            new object[] { Encoding.UTF8.GetBytes("not a discovery packet"), IPAddress.Loopback }) as DeviceInfo);
    }

    [Fact]
    public async Task RealBatchProbe_ReportsEveryAddress_AndFinishesWithoutReply()
    {
        // TEST-NET-3（RFC 5737）：不可达地址，验证共享 socket 路径不会挂住、
        // 不因 ICMP 异常中断，且进度仍逐地址如实上报（UI 侧采集器依赖这一点）。
        var probe = CreateRealProbe();
        var addresses = new[]
        {
            IPAddress.Parse("203.0.113.1"),
            IPAddress.Parse("203.0.113.2"),
            IPAddress.Parse("203.0.113.3"),
            IPAddress.Parse("203.0.113.4")
        };
        var progress = new SyncProgress();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var devices = await probe.ProbeBatchAsync(addresses, progress, TestContext.Current.CancellationToken);

        clock.Stop();
        Assert.Empty(devices);
        Assert.Equal(addresses.Length, progress.Items.Count);
        Assert.Equal(100, progress.Items.Max(item => item.Percent));
        Assert.True(clock.ElapsedMilliseconds < 5000,
            $"批量探测不应等待逐地址超时，实际 {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RealBatchProbe_CancelAlreadyRequested_DoesNotStartScan()
    {
        var probe = CreateRealProbe();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Task.Factory.StartNew 在令牌已取消时直接返回 Canceled Task，工作体不会执行
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeBatchAsync(
                new[] { IPAddress.Parse("203.0.113.1") }, null, cts.Token));
    }

    [Fact]
    public async Task RealBatchProbe_DiscoversPeerReply_OverSharedSocket()
    {
        // 端到端：假对端监听发现端口，回一个标准发现应答；
        // 验证共享 socket 路径确实能收到、解析并按回包源地址归属设备。
        UdpClient? peer;
        try
        {
            peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 5236));
        }
        catch (SocketException)
        {
            return; // 端口被本机运行的发现服务占用，环境不具备条件
        }

        using (peer)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var responder = Task.Run(async () =>
            {
                var request = await peer.ReceiveAsync(cts.Token);
                var reply = Encoding.UTF8.GetBytes(
                    "FileShareDiscovery" + JsonSerializer.Serialize(Peer(42)));
                await peer.SendAsync(reply, reply.Length, request.RemoteEndPoint);
            }, TestContext.Current.CancellationToken);

            var probe = CreateRealProbe();
            var devices = await probe.ProbeBatchAsync(
                new[] { IPAddress.Loopback }, null, CancellationToken.None);
            await responder;

            var device = Assert.Single(devices);
            Assert.Equal("peer-42", device.DeviceId);
            Assert.Equal("127.0.0.1", device.IpAddress);
        }
    }

    /// <summary>上报第一批进度时立刻取消，模拟扫描进行中用户点「停止」。</summary>
    private sealed class CancellingProgress : IProgress<SubnetScanProgress>
    {
        private readonly CancellationTokenSource _cts;
        public int Count;

        public CancellingProgress(CancellationTokenSource cts) => _cts = cts;

        public void Report(SubnetScanProgress value)
        {
            Interlocked.Increment(ref Count);
            _cts.Cancel();
        }
    }

    /// <summary>批量探测器：取消时只停止派发、返回已发现设备，不抛异常。</summary>
    private sealed class NonThrowingBatchProbe : IDeviceProbeService, IBatchDeviceProbeService
    {
        public Task<DeviceInfo?> ProbeAsync(string a, CancellationToken ct = default)
            => Task.FromResult<DeviceInfo?>(null);

        public Task<DeviceInfo?> ProbeAsync(IPAddress a, int p, CancellationToken ct = default)
            => Task.FromResult<DeviceInfo?>(null);

        public Task<IReadOnlyList<DeviceInfo>> ProbeBatchAsync(
            IReadOnlyList<IPAddress> addresses,
            IProgress<SubnetScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            for (var i = 0; i < addresses.Count && !cancellationToken.IsCancellationRequested; i++)
            {
                progress?.Report(new SubnetScanProgress(
                    i + 1, addresses.Count, addresses[i].ToString(), null));
            }

            return Task.FromResult<IReadOnlyList<DeviceInfo>>(Array.Empty<DeviceInfo>());
        }
    }

    [Fact]
    public async Task RealBatchProbe_CancelDuringScan_ReturnsWithoutException()
    {
        // 用户报的场景：扫描到一半点停止。后台专用线程必须正常返回，
        // 不能抛 OCE 让 Task 变成 Canceled/Faulted 进而产生未观察异常。
        var probe = CreateRealProbe();
        var addresses = Enumerable.Range(0, 300)
            .Select(i => IPAddress.Parse($"192.0.2.{i % 254 + 1}"))
            .ToArray();
        using var cts = new CancellationTokenSource();
        var progress = new CancellingProgress(cts);

        var devices = await probe.ProbeBatchAsync(addresses, progress, cts.Token);

        Assert.Empty(devices);
        Assert.True(progress.Count > 0, "取消前至少应完成一批并如实上报进度");
    }

    [Fact]
    public async Task ScannerBatchPath_CancelDuringScan_ReturnsPartialResultWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        var progress = new CancellingProgress(cts);
        var scanner = new SubnetScanner(new NonThrowingBatchProbe());

        var devices = await scanner.ScanAsync("203.0.113.0/29", progress, cts.Token);

        Assert.Empty(devices);
        Assert.True(progress.Count > 0);
    }
}
