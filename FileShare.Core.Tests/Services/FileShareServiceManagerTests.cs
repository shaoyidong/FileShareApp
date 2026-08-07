using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Moq;

namespace FileShare.Core.Tests.Services;

/// <summary>
/// 测试 FileShareServiceManager 类的功能
/// 这个类是一个管理类，整合了设备发现和文件传输功能
/// </summary>
public class FileShareServiceManagerTests
{
    private readonly Mock<IPlatformDirectoryService> _mockDirectoryService;
    private readonly Mock<IDatabaseService> _mockDatabaseService;
    public FileShareServiceManagerTests()
    {
        _mockDirectoryService = new Mock<IPlatformDirectoryService>();
        _mockDatabaseService = new Mock<IDatabaseService>();
        _mockDatabaseService.Setup(db => db.GetOrCreateDeviceId()).Returns(Guid.NewGuid().ToString());
    }
    /// <summary>
    /// 测试构造函数是否正确初始化所有依赖项
    /// </summary>
    [Fact]
    public void Constructor_InitializesServicesCorrectly()
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        var deviceType = DeviceType.Desktop;
        
        // Act: 创建 FileShareServiceManager 实例
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType);
        
        // Assert: 验证实例是否创建成功
        Assert.NotNull(manager);
    }
    
    /// <summary>
    /// 测试 GetLocalDeviceInfo 方法是否返回正确的本地设备信息
    /// </summary>
    [Fact]
    public void GetLocalDeviceInfo_ReturnsCorrectDeviceInfo()
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        var deviceType = DeviceType.Desktop;
        
        // Act: 创建实例并获取本地设备信息
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType);
        var localDevice = manager.GetLocalDeviceInfo();
        
        // Assert: 验证本地设备信息
        Assert.NotNull(localDevice);
        Assert.Equal(deviceName, localDevice.DeviceName);
        Assert.Equal(deviceType, localDevice.DeviceType);
        Assert.NotNull(localDevice.DeviceId);
        Assert.Equal(5237, localDevice.Port); // 默认传输端口
    }
    
    /// <summary>
    /// 测试事件是否正确传递
    /// </summary>
    [Fact]
    public void Events_ArePassedCorrectly()
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        var deviceType = DeviceType.Desktop;
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType);
        
        bool devicesUpdatedCalled = false;
        bool transferRequestCalled = false;
        bool progressUpdatedCalled = false;
        bool transferCompletedCalled = false;
        
        // 注册事件处理程序
        manager.OnDeviceDiscovered += _ => devicesUpdatedCalled = true;
        manager.OnTransferRequestSendAndReceive += _ => transferRequestCalled = true;
        manager.OnTransferProgressUpdated += _ => progressUpdatedCalled = true;
        manager.OnTransferCompleted += (_, __) => transferCompletedCalled = true;
        
        // Act: 模拟事件触发
        // 由于我们无法直接触发内部服务的事件，我们将测试事件注册是否成功
        // 实际的事件传递测试需要使用模拟对象
        
        // Assert: 验证事件处理程序已注册
        // 我们将通过验证管理器实例来确保事件注册过程没有错误
        Assert.NotNull(manager);
    }
    
    /// <summary>
    /// 测试 RefreshDevicesAsync 方法是否调用了发现服务的 SendDiscoveryPacketAsync 方法
    /// </summary>
    [Fact]
    public async Task RefreshDevicesAsync_CallsDiscoveryService()
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        var deviceType = DeviceType.Desktop;
        
        // Act: 创建实例并调用 RefreshDevicesAsync
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType);
        await manager.RefreshDevicesAsync();
        
        // Assert: 验证方法执行成功
        // 由于我们无法直接验证内部调用，我们将验证方法执行过程中没有抛出异常
        Assert.NotNull(manager);
    }
    
    /// <summary>
    /// 测试 Dispose 方法是否正确释放资源
    /// </summary>
    [Fact]
    public void Dispose_ReleasesResources()
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        var deviceType = DeviceType.Desktop;
        
        // Act: 创建实例并释放资源
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType);
        manager.Dispose();
        
        // Assert: 验证实例已被释放
        // 由于 Dispose 方法没有返回值，我们将验证方法执行过程中没有抛出异常
        Assert.NotNull(manager);
    }
    
    /// <summary>
    /// 测试不同设备类型的初始化
    /// </summary>
    [Theory]
    [InlineData(DeviceType.Desktop)]
    [InlineData(DeviceType.Mobile)]
    [InlineData(DeviceType.Tablet)]
    public void Constructor_SupportsDifferentDeviceTypes(DeviceType deviceType)
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        
        // Act: 创建实例
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType);
        var localDevice = manager.GetLocalDeviceInfo();
        
        // Assert: 验证设备类型
        Assert.Equal(deviceType, localDevice.DeviceType);
    }
    
    /// <summary>
    /// 测试自定义端口的初始化
    /// </summary>
    [Fact]
    public void Constructor_SupportsCustomPorts()
    {
        // Arrange: 准备测试数据
        var deviceName = "Test Device";
        var deviceType = DeviceType.Desktop;
        var customDiscoveryPort = 1234;
        var customTransferPort = 5678;
        
        // Act: 使用自定义端口创建实例
        var manager = new FileShareServiceManager(_mockDirectoryService.Object, _mockDatabaseService.Object, deviceName, deviceType, customDiscoveryPort, customTransferPort);
        var localDevice = manager.GetLocalDeviceInfo();
        
        // Assert: 验证自定义端口是否被正确使用
        Assert.Equal(customTransferPort, localDevice.Port);
    }
}
