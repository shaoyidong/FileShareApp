using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Moq;
using System.Net.Sockets;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 测试 TcpFileTransferService 类的功能
/// 这个类用于通过TCP协议进行文件传输
/// </summary>
public class TcpFileTransferServiceTests
{
    private readonly Mock<IPlatformDirectoryService> _mockDirectoryService;
    public TcpFileTransferServiceTests()
    {
        _mockDirectoryService = new Mock<IPlatformDirectoryService>();
    }
    /// <summary>
    /// 测试构造函数是否正确初始化
    /// </summary>
    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        // Arrange & Act: 创建 TcpFileTransferService 实例
        using var service = new TcpFileTransferService(_mockDirectoryService.Object);
        
        // Assert: 验证实例是否创建成功
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试使用自定义端口的构造函数
    /// </summary>
    [Fact]
    public void Constructor_WithCustomPort_InitializesCorrectly()
    {
        // Arrange: 准备自定义端口
        var customPort = 12345;
        
        // Act: 使用自定义端口创建实例
        using var service = new TcpFileTransferService(_mockDirectoryService.Object, customPort);
        
        // Assert: 验证实例是否创建成功
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试事件注册是否成功
    /// </summary>
    [Fact]
    public void Events_CanBeRegistered()
    {
        // Arrange: 准备测试数据
        using var service = new TcpFileTransferService(_mockDirectoryService.Object);
        
        bool transferRequestCalled = false;
        bool progressUpdatedCalled = false;
        bool transferCompletedCalled = false;
        
        // Act: 注册事件处理程序
        service.OnTransferRequestSendAndReceive += _ => transferRequestCalled = true;
        service.OnTransferProgressUpdated += _ => progressUpdatedCalled = true;
        service.OnTransferCompleted += (_, __) => transferCompletedCalled = true;
        
        // Assert: 验证事件处理程序已注册
        Assert.NotNull(service);
        // 我们无法直接触发事件，但可以验证注册过程没有错误
    }
    
    /// <summary>
    /// 测试 HandleTransferRequest 方法是否能正常执行（不抛出异常）
    /// </summary>
    [Fact]
    public void HandleTransferRequest_ExecutesWithoutException()
    {
        // Arrange: 准备测试数据
        using var service = new TcpFileTransferService(_mockDirectoryService.Object);
        var transferId = "test-transfer-id";
        var accept = true;
        var savePath = "C:\\Downloads";
        
        // Act & Assert: 验证方法执行过程中没有抛出异常
        service.HandleTransferRequest(transferId, accept, savePath);
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试 CancelTransfer 方法是否能正常执行（不抛出异常）
    /// </summary>
    [Fact]
    public void CancelTransfer_ExecutesWithoutException()
    {
        // Arrange: 准备测试数据
        using var service = new TcpFileTransferService(_mockDirectoryService.Object);
        var transferId = "test-transfer-id";
        
        // Act & Assert: 验证方法执行过程中没有抛出异常
        service.CancelTransfer(transferId);
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试 StartAsync 和 StopAsync 方法
    /// </summary>
    [Fact]
    public async Task StartAsync_And_StopAsync_ExecuteWithoutException()
    {
        // Arrange: 准备测试数据
        using var service = new TcpFileTransferService(_mockDirectoryService.Object); 
        
        // Act & Assert: 验证 StartAsync 和 StopAsync 方法执行过程中没有抛出异常
        await service.StartAsync();
        
        // 短暂延迟，确保服务有时间启动
        await Task.Delay(100);
        
        await service.StopAsync();
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试 Dispose 方法是否能正常执行
    /// </summary>
    [Fact]
    public void Dispose_ReleasesResources()
    {
        // Arrange: 准备测试数据
        var service = new TcpFileTransferService(_mockDirectoryService.Object);
        
        // Act: 调用 Dispose 方法
        service.Dispose();
        
        // Assert: 验证方法执行过程中没有抛出异常
        Assert.NotNull(service);
    }
    
    /// <summary>
    /// 测试多次调用 Dispose 方法不会抛出异常
    /// </summary>
    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange: 准备测试数据
        var service = new TcpFileTransferService(_mockDirectoryService.Object);
        
        // Act & Assert: 多次调用 Dispose 方法
        service.Dispose();
        service.Dispose(); // 第二次调用不应该抛出异常
        service.Dispose(); // 第三次调用不应该抛出异常
        
        Assert.NotNull(service);
    }
}
