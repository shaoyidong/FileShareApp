using FileShare.Core.Models;

namespace FileShare.Core.Tests.Models;

/// <summary>
/// 测试 FileTransferInfo 类的功能
/// FileTransferInfo 类用于表示文件传输的详细信息
/// </summary>
public class FileTransferInfoTests
{
    /// <summary>
    /// 测试 FileTransferInfo 构造函数是否正确设置所有属性
    /// </summary>
    [Fact]
    public void FileTransferInfo_Constructor_SetsPropertiesCorrectly()
    {
        // Arrange: 准备测试数据
        var transferId = "test-transfer-id";
        var fileName = "test-file.txt";
        var fileSize = 1024 * 1024; // 1MB
        var senderId = "sender-123";
        var receiverId = "receiver-456";
        var savePath = "C:\\Downloads";
        
        // Act: 创建 FileTransferInfo 实例并设置属性
        var transferInfo = new FileTransferInfo
        {
            TransferId = transferId,
            FileName = fileName,
            FileSize = fileSize,
            SenderId = senderId,
            ReceiverId = receiverId,
            SavePath = savePath
        };
        
        // Assert: 验证所有属性是否被正确设置
        Assert.Equal(transferId, transferInfo.TransferId);
        Assert.Equal(fileName, transferInfo.FileName);
        Assert.Equal(fileSize, transferInfo.FileSize);
        Assert.Equal(senderId, transferInfo.SenderId);
        Assert.Equal(receiverId, transferInfo.ReceiverId);
        Assert.Equal(savePath, transferInfo.SavePath);
        Assert.Equal(TransferStatus.Pending, transferInfo.Status); // 默认状态应为 Pending
        Assert.Equal(0, transferInfo.TransferredSize); // 默认已传输大小应为 0
    }
    
    /// <summary>
    /// 测试 ProgressPercentage 属性是否正确计算传输进度
    /// </summary>
    [Theory]
    [InlineData(0, 1000, 0)] // 0 bytes transferred, 1000 bytes total = 0% progress
    [InlineData(500, 1000, 50)] // 500 bytes transferred, 1000 bytes total = 50% progress
    [InlineData(1000, 1000, 100)] // 1000 bytes transferred, 1000 bytes total = 100% progress
    [InlineData(1500, 1000, 150)] // 1500 bytes transferred, 1000 bytes total = 150% progress (超额传输)
    [InlineData(0, 0, 0)] // 0 bytes total = 0% progress (避免除以零)
    public void ProgressPercentage_CalculatesCorrectly(long transferredSize, long fileSize, double expectedProgress)
    {
        // Arrange: 创建 FileTransferInfo 实例并设置基本属性
        var transferInfo = new FileTransferInfo
        {
            TransferId = "test-id",
            FileName = "test.txt",
            FileSize = fileSize,
            SenderId = "sender",
            ReceiverId = "receiver",
            TransferredSize = transferredSize
        };
        
        // Act: 获取计算的进度百分比
        var actualProgress = transferInfo.ProgressPercentage;
        
        // Assert: 验证进度百分比是否正确计算
        Assert.Equal(expectedProgress, actualProgress, 2); // 允许 2 位小数的误差
    }
    
    /// <summary>
    /// 测试传输状态枚举值是否正确
    /// </summary>
    [Fact]
    public void TransferStatus_EnumValues_AreCorrect()
    {
        // Arrange & Act: 获取所有枚举值
        var pendingValue = (int)TransferStatus.Pending;
        var transferringValue = (int)TransferStatus.Transferring;
        var completedValue = (int)TransferStatus.Completed;
        var failedValue = (int)TransferStatus.Failed;
        var cancelledValue = (int)TransferStatus.Cancelled;
        
        // Assert: 验证枚举值是否符合预期
        Assert.Equal(0, pendingValue);
        Assert.Equal(1, transferringValue);
        Assert.Equal(2, completedValue);
        Assert.Equal(3, failedValue);
        Assert.Equal(4, cancelledValue);
    }
    
    /// <summary>
    /// 测试传输状态变更是否正确
    /// </summary>
    [Fact]
    public void Status_Property_ChangesCorrectly()
    {
        // Arrange: 创建 FileTransferInfo 实例
        var transferInfo = new FileTransferInfo
        {
            TransferId = "test-id",
            FileName = "test.txt",
            FileSize = 1000,
            SenderId = "sender",
            ReceiverId = "receiver"
        };
        
        // Act: 依次变更传输状态
        transferInfo.Status = TransferStatus.Transferring;
        var isTransferring = transferInfo.Status == TransferStatus.Transferring;
        
        transferInfo.Status = TransferStatus.Completed;
        var isCompleted = transferInfo.Status == TransferStatus.Completed;
        
        transferInfo.Status = TransferStatus.Failed;
        var isFailed = transferInfo.Status == TransferStatus.Failed;
        
        transferInfo.Status = TransferStatus.Cancelled;
        var isCancelled = transferInfo.Status == TransferStatus.Cancelled;
        
        // Assert: 验证状态变更是否正确
        Assert.True(isTransferring);
        Assert.True(isCompleted);
        Assert.True(isFailed);
        Assert.True(isCancelled);
    }
    
    /// <summary>
    /// 测试 TransferredSize 属性变更时，ProgressPercentage 是否自动更新
    /// </summary>
    [Fact]
    public void ProgressPercentage_UpdatesWhenTransferredSizeChanges()
    {
        // Arrange: 创建 FileTransferInfo 实例
        var transferInfo = new FileTransferInfo
        {
            TransferId = "test-id",
            FileName = "test.txt",
            FileSize = 1000,
            SenderId = "sender",
            ReceiverId = "receiver"
        };
        
        // Act: 初始进度应为 0%
        var initialProgress = transferInfo.ProgressPercentage;
        
        // 传输 500 字节，进度应为 50%
        transferInfo.TransferredSize = 500;
        var progressAfter500Bytes = transferInfo.ProgressPercentage;
        
        // 传输完整 1000 字节，进度应为 100%
        transferInfo.TransferredSize = 1000;
        var progressAfterFullTransfer = transferInfo.ProgressPercentage;
        
        // Assert: 验证进度百分比是否随已传输大小变化而更新
        Assert.Equal(0, initialProgress);
        Assert.Equal(50, progressAfter500Bytes);
        Assert.Equal(100, progressAfterFullTransfer);
    }
    
    /// <summary>
    /// 测试 SavePath 属性的可空特性
    /// </summary>
    [Fact]
    public void SavePath_CanBeNull()
    {
        // Arrange: 创建 FileTransferInfo 实例
        var transferInfo = new FileTransferInfo
        {
            TransferId = "test-id",
            FileName = "test.txt",
            FileSize = 1000,
            SenderId = "sender",
            ReceiverId = "receiver"
        };
        
        // Act: 设置 SavePath 为 null
        transferInfo.SavePath = null;
        var isNull = transferInfo.SavePath == null;
        
        // 设置 SavePath 为有效路径
        var validPath = "C:\\Downloads\\Files";
        transferInfo.SavePath = validPath;
        var isNotNull = transferInfo.SavePath == validPath;
        
        // Assert: 验证 SavePath 可以是 null 或有效路径
        Assert.True(isNull);
        Assert.True(isNotNull);
    }
    
    /// <summary>
    /// 测试 FileTransferInfo 的所有属性设置器是否正确工作
    /// </summary>
    [Fact]
    public void FileTransferInfo_AllPropertySetters_WorkCorrectly()
    {
        // Arrange: 创建初始 FileTransferInfo 实例
        var transferInfo = new FileTransferInfo
        {
            TransferId = "initial-id",
            FileName = "initial-file.txt",
            FileSize = 1000,
            SenderId = "initial-sender",
            ReceiverId = "initial-receiver"
        };
        
        // Act: 更新所有可更改的属性
        var newTransferId = "updated-id";
        var newFileName = "updated-file.txt";
        var newFileSize = 2048;
        var newSenderId = "updated-sender";
        var newReceiverId = "updated-receiver";
        var newTransferredSize = 512;
        var newStatus = TransferStatus.Transferring;
        var newSavePath = "C:\\Updated\\Path";
        
        // 更新属性
        transferInfo.TransferId = newTransferId;
        transferInfo.FileName = newFileName;
        transferInfo.FileSize = newFileSize;
        transferInfo.SenderId = newSenderId;
        transferInfo.ReceiverId = newReceiverId;
        transferInfo.TransferredSize = newTransferredSize;
        transferInfo.Status = newStatus;
        transferInfo.SavePath = newSavePath;
        
        // Assert: 验证所有属性都已正确更新
        Assert.Equal(newTransferId, transferInfo.TransferId);
        Assert.Equal(newFileName, transferInfo.FileName);
        Assert.Equal(newFileSize, transferInfo.FileSize);
        Assert.Equal(newSenderId, transferInfo.SenderId);
        Assert.Equal(newReceiverId, transferInfo.ReceiverId);
        Assert.Equal(newTransferredSize, transferInfo.TransferredSize);
        Assert.Equal(newStatus, transferInfo.Status);
        Assert.Equal(newSavePath, transferInfo.SavePath);
    }
}