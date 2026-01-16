using FileShare.Core.Models;

namespace FileShare.Core.Tests.Models;

/// <summary>
/// 测试 DeviceInfo 类的功能
/// DeviceInfo 类用于表示局域网中的设备信息
/// </summary>
public class DeviceInfoTests
{
    /// <summary>
    /// 测试 DeviceInfo 构造函数是否正确设置所有属性
    /// </summary>
    [Fact]
    public void DeviceInfo_Constructor_SetsPropertiesCorrectly()
    {
        // Arrange: 准备测试数据
        var deviceId = "test-device-id"; // 设备唯一标识符
        var deviceName = "Test Device"; // 设备名称
        var ipAddress = "192.168.1.100"; // 设备IP地址
        var port = 5000; // 服务端口
        var deviceType = DeviceType.Desktop; // 设备类型
        var lastSeen = DateTime.Now; // 最后在线时间
        
        // Act: 创建 DeviceInfo 实例并设置属性
        var deviceInfo = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            IpAddress = ipAddress,
            Port = port,
            DeviceType = deviceType,
            LastSeen = lastSeen
        };
        
        // Assert: 验证所有属性是否被正确设置
        Assert.Equal(deviceId, deviceInfo.DeviceId);
        Assert.Equal(deviceName, deviceInfo.DeviceName);
        Assert.Equal(ipAddress, deviceInfo.IpAddress);
        Assert.Equal(port, deviceInfo.Port);
        Assert.Equal(deviceType, deviceInfo.DeviceType);
        Assert.Equal(lastSeen, deviceInfo.LastSeen);
    }
    
    /// <summary>
    /// 测试 DeviceInfo 属性设置器是否正确工作
    /// </summary>
    [Fact]
    public void DeviceInfo_PropertySetters_WorkCorrectly()
    {
        // Arrange: 创建一个空的 DeviceInfo 实例和测试数据
        var deviceInfo = new DeviceInfo();
        var newDeviceName = "Updated Device"; // 新的设备名称
        var newPort = 8080; // 新的服务端口
        var newDeviceType = DeviceType.Mobile; // 新的设备类型
        
        // Act: 设置不同的属性值
        deviceInfo.DeviceName = newDeviceName;
        deviceInfo.Port = newPort;
        deviceInfo.DeviceType = newDeviceType;
        
        // Assert: 验证属性是否被正确更新
        Assert.Equal(newDeviceName, deviceInfo.DeviceName);
        Assert.Equal(newPort, deviceInfo.Port);
        Assert.Equal(newDeviceType, deviceInfo.DeviceType);
    }
    
    /// <summary>
    /// 测试 DeviceType 枚举值是否正确
    /// </summary>
    [Fact]
    public void DeviceType_EnumValues_AreCorrect()
    {
        // Arrange & Act: 获取枚举值
        var desktopValue = (int)DeviceType.Desktop;
        var mobileValue = (int)DeviceType.Mobile;
        var tabletValue = (int)DeviceType.Tablet;
        
        // Assert: 验证枚举值是否符合预期
        Assert.Equal(0, desktopValue);
        Assert.Equal(1, mobileValue);
        Assert.Equal(2, tabletValue);
    }
    
    /// <summary>
    /// 测试不同设备类型的赋值是否正确
    /// </summary>
    [Fact]
    public void DeviceInfo_DeviceType_AssignsCorrectly()
    {
        // Arrange: 创建 DeviceInfo 实例
        var deviceInfo = new DeviceInfo();
        
        // Act: 依次赋值不同的设备类型
        deviceInfo.DeviceType = DeviceType.Desktop;
        var isDesktop = deviceInfo.DeviceType == DeviceType.Desktop;
        
        deviceInfo.DeviceType = DeviceType.Mobile;
        var isMobile = deviceInfo.DeviceType == DeviceType.Mobile;
        
        deviceInfo.DeviceType = DeviceType.Tablet;
        var isTablet = deviceInfo.DeviceType == DeviceType.Tablet;
        
        // Assert: 验证设备类型赋值是否正确
        Assert.True(isDesktop);
        Assert.True(isMobile);
        Assert.True(isTablet);
    }
}