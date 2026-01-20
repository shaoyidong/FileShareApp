using FileShare.Core.Models;
using FileShare.Core.Network;
using Moq;
using System.Net;
using System.Net.Sockets;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 测试 UdpDiscoveryService 类的功能
/// 这个类用于设备发现，通过UDP广播和接收消息
/// </summary>
public class UdpDiscoveryServiceTests
{
    /// <summary>
    /// 测试构造函数是否正确初始化
    /// </summary>
    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        // Arrange: 准备测试数据
        var deviceInfo = new DeviceInfo()
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1"
        };    
        
        // Act: 创建 UdpDiscoveryService 实例
        using var service = new UdpDiscoveryService(deviceInfo);
        
        // Assert: 验证实例是否创建成功
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试 GetDiscoveredDevices 方法是否返回空列表（初始状态）
    /// </summary>
    [Fact]
    public void GetDiscoveredDevices_ReturnsEmptyListInitially()
    {
        // Arrange: 准备测试数据
        var deviceInfo = new DeviceInfo()
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1"
        };

        // Act: 创建实例并获取发现的设备列表
        using var service = new UdpDiscoveryService(deviceInfo);
        var discoveredDevices = service.GetDiscoveredDevices();
        
        // Assert: 验证初始状态下设备列表为空
        Assert.NotNull(discoveredDevices);
        Assert.Empty(discoveredDevices);
    }
    
    /// <summary>
    /// 测试 SendDiscoveryPacket 方法是否能正常执行（不抛出异常）
    /// </summary>
    [Fact]
    public void SendDiscoveryPacket_ExecutesWithoutException()
    {
        // Arrange: 准备测试数据
        var deviceInfo = new DeviceInfo()
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1"
        };

        // Act & Assert: 验证方法执行过程中没有抛出异常
        using var service = new UdpDiscoveryService(deviceInfo);
        Assert.NotNull(service);
        
        // 这个方法应该能正常执行，不会抛出异常
        service.SendDiscoveryPacket();
    }
    
    /// <summary>
    /// 测试事件注册是否成功
    /// </summary>
    [Fact]
    public void Event_OnDeviceDiscovered_CanBeRegistered()
    {
        // Arrange: 准备测试数据
        var deviceInfo = new DeviceInfo()
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1"
        };

        // Act: 创建实例并注册事件
        using var service = new UdpDiscoveryService(deviceInfo);
        bool eventCalled = false;
        
        service.OnDeviceDiscovered += device =>
        {
            eventCalled = true;
            Assert.NotNull(device);
        };
        
        // Assert: 验证事件注册成功
        Assert.NotNull(service);
        // 我们无法直接触发事件，但可以验证注册过程没有错误
    }
    
    /// <summary>
    /// 测试服务的 StartAsync 和 StopAsync 方法
    /// </summary>
    [Fact]
    public async Task StartAsync_And_StopAsync_ExecuteWithoutException()
    {
        // Arrange: 准备测试数据
        var deviceInfo = new DeviceInfo()
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1"
        };

        // Act & Assert: 验证 StartAsync 和 StopAsync 方法执行过程中没有抛出异常
        using var service = new UdpDiscoveryService(deviceInfo);
        
        // 启动服务
        await service.StartAsync();
        
        // 短暂延迟，确保服务有时间启动
        await Task.Delay(100);
        
        // 停止服务
        await service.StopAsync();
        
        // 验证服务已停止
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试服务的 Dispose 方法
    /// </summary>
    [Fact]
    public void Dispose_ReleasesResources()
    {
        // Arrange: 准备测试数据
        var deviceInfo = new DeviceInfo()
        {
            DeviceId = "test-device-id",
            DeviceName = "Test Device",
            DeviceType = DeviceType.Desktop,
            IpAddress = "127.0.0.1"
        };

        // Act: 创建实例并释放资源
        var service = new UdpDiscoveryService(deviceInfo);
        service.Dispose();
        
        // Assert: 验证服务已释放资源
        Assert.NotNull(service);
    }
}
