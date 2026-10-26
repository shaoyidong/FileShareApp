using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// <see cref="UdpMulticastDiscoveryService"/> 的基础行为测试。
/// <para>这些用例不依赖网络上的其他设备，只验证构造、启动/停止与事件注册等生命周期行为。</para>
/// </summary>
public class UdpDiscoveryServiceTests
{
    private static DeviceInfo CreateLocalDevice()
    {
        return new DeviceInfo
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1",
            Port = 5237
        };
    }

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        using var service = new UdpMulticastDiscoveryService(CreateLocalDevice(), "224.0.0.167", 35317);
        Assert.NotNull(service);
    }

    [Fact]
    public void Event_OnDeviceDiscovered_CanBeRegistered()
    {
        using var service = new UdpMulticastDiscoveryService(CreateLocalDevice(), "224.0.0.167", 35317);

        var called = false;
        service.OnDeviceDiscovered += device =>
        {
            called = true;
            Assert.NotNull(device);
        };
        service.OnDeviceRemoved += deviceId => Assert.NotNull(deviceId);

        Assert.False(called);
    }

    [Fact]
    public async Task StartAsync_And_StopAsync_ExecuteWithoutException()
    {
        using var service = new UdpMulticastDiscoveryService(CreateLocalDevice(), "224.0.0.167", 35317);

        await service.StartAsync();
        await Task.Delay(50);
        await service.SendServiceQueryAsync();
        await service.StopAsync();

        Assert.NotNull(service);
    }

    [Fact]
    public void Dispose_ReleasesResources()
    {
        var service = new UdpMulticastDiscoveryService(CreateLocalDevice(), "224.0.0.167", 35317);
        service.Dispose();

        // 重复释放不应抛异常
        service.Dispose();
        Assert.NotNull(service);
    }
}
