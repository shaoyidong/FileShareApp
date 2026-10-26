using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;
using System.Net;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 网段扫描测试（不依赖真实网络，使用假的探测器）。
/// </summary>
public class SubnetScannerTests
{
    /// <summary>同步执行回调的进度实现，避免 Progress&lt;T&gt; 的线程池回调导致断言竞态。</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();

        public void Report(T value)
        {
            lock (Items)
            {
                Items.Add(value);
            }
        }
    }

    private sealed class FakeProbeService : IDeviceProbeService
    {
        private readonly HashSet<string> _reachable;

        public FakeProbeService(params string[] reachable)
        {
            _reachable = new HashSet<string>(reachable);
        }

        public List<IPAddress> Probed { get; } = new();

        public Task<DeviceInfo?> ProbeAsync(string address, CancellationToken cancellationToken = default)
            => Task.FromResult<DeviceInfo?>(null);

        public Task<DeviceInfo?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
        {
            lock (Probed)
            {
                Probed.Add(address);
            }

            if (!_reachable.Contains(address.ToString()))
            {
                return Task.FromResult<DeviceInfo?>(null);
            }

            return Task.FromResult<DeviceInfo?>(new DeviceInfo
            {
                DeviceId = "fake-" + address,
                DeviceName = "Fake " + address,
                IpAddress = address.ToString(),
                Port = port,
                DeviceType = DeviceType.Desktop
            });
        }
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0", 24)]
    [InlineData("192.168.1.0", "192.168.1.0", 24)]
    [InlineData("10.0.0.0/20", "10.0.0.0", 20)]
    [InlineData("192.168.1.77/26", "192.168.1.77", 26)]
    public void TryParseCidr_AcceptsValidInput(string input, string expectedNetwork, int expectedPrefix)
    {
        var ok = SubnetScanner.TryParseCidr(input, out var network, out var prefix, out var error);

        Assert.True(ok, error);
        Assert.Equal(expectedNetwork, network!.ToString());
        Assert.Equal(expectedPrefix, prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("192.168.1.0/16")]
    [InlineData("192.168.1.0/33")]
    [InlineData("::1/64")]
    [InlineData("not-a-network")]
    public void TryParseCidr_RejectsInvalidInput(string? input)
    {
        var ok = SubnetScanner.TryParseCidr(input, out _, out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EnumerateHosts_SkipsNetworkAndBroadcastAddresses()
    {
        var hosts = SubnetScanner.EnumerateHosts(IPAddress.Parse("192.168.1.0"), 24);

        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.254", hosts[^1].ToString());
    }

    [Fact]
    public void EnumerateHosts_HandlesSmallPrefixes()
    {
        var hosts = SubnetScanner.EnumerateHosts(IPAddress.Parse("192.168.1.0"), 30);

        Assert.Equal(2, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.2", hosts[1].ToString());
    }

    [Fact]
    public void EnumerateHosts_HandlesSingleHostPrefix()
    {
        var hosts = SubnetScanner.EnumerateHosts(IPAddress.Parse("192.168.1.5"), 32);

        Assert.Single(hosts);
        Assert.Equal("192.168.1.5", hosts[0].ToString());
    }

    [Fact]
    public async Task ScanAsync_ReturnsOnlyReachableDevices()
    {
        // 使用 TEST-NET-3 网段（RFC 5737），不会与本机地址冲突
        var probe = new FakeProbeService("203.0.113.1");
        var scanner = new SubnetScanner(probe);
        var progress = new SyncProgress<SubnetScanProgress>();

        var devices = await scanner.ScanAsync("203.0.113.0/30", progress);

        Assert.Single(devices);
        Assert.Equal("203.0.113.1", devices[0].IpAddress);
        Assert.Equal(2, probe.Probed.Count);
        Assert.Equal(2, progress.Items.Count);
        Assert.Contains(progress.Items, item => item.FoundDevice is not null);
        Assert.Equal(100, progress.Items[^1].Percent);
    }

    [Fact]
    public async Task ScanAsync_ThrowsOnInvalidCidr()
    {
        var scanner = new SubnetScanner(new FakeProbeService());

        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanAsync("192.168.1.0/8"));
    }

    [Fact]
    public async Task ScanAsync_CanBeCancelled()
    {
        var scanner = new SubnetScanner(new FakeProbeService());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync("203.0.113.0/24", null, cts.Token));
    }
}
