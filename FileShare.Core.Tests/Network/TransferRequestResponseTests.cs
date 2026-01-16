using FileShare.Core.Network;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 测试 TransferRequest 和 TransferResponse 类的功能
/// 这些类用于在 TCP 文件传输过程中传递请求和响应信息
/// </summary>
public class TransferRequestResponseTests
{
    /// <summary>
    /// 测试 TransferRequest 构造函数是否正确设置所有属性
    /// </summary>
    [Fact]
    public void TransferRequest_Constructor_SetsPropertiesCorrectly()
    {
        // Arrange: 准备测试数据
        var transferId = "test-transfer-id";
        var type = TransferRequestType.SendFileRequest;
        var fileName = "test-file.txt";
        var fileSize = 1024 * 1024; // 1MB
        var senderId = "sender-123";
        var receiverId = "receiver-456";
        
        // Act: 创建 TransferRequest 实例并设置属性
        var request = new TransferRequest
        {
            TransferId = transferId,
            Type = type,
            FileName = fileName,
            FileSize = fileSize,
            SenderId = senderId,
            ReceiverId = receiverId
        };
        
        // Assert: 验证所有属性是否被正确设置
        Assert.Equal(transferId, request.TransferId);
        Assert.Equal(type, request.Type);
        Assert.Equal(fileName, request.FileName);
        Assert.Equal(fileSize, request.FileSize);
        Assert.Equal(senderId, request.SenderId);
        Assert.Equal(receiverId, request.ReceiverId);
    }
    
    /// <summary>
    /// 测试 TransferResponse 构造函数是否正确设置所有属性
    /// </summary>
    [Fact]
    public void TransferResponse_Constructor_SetsPropertiesCorrectly()
    {
        // Arrange: 准备测试数据
        var transferId = "test-transfer-id";
        var accepted = true;
        var message = "Transfer accepted";
        
        // Act: 创建 TransferResponse 实例并设置属性
        var response = new TransferResponse
        {
            TransferId = transferId,
            Accepted = accepted,
            Message = message
        };
        
        // Assert: 验证所有属性是否被正确设置
        Assert.Equal(transferId, response.TransferId);
        Assert.Equal(accepted, response.Accepted);
        Assert.Equal(message, response.Message);
    }
    
    /// <summary>
    /// 测试 TransferRequestType 枚举值是否正确
    /// </summary>
    [Fact]
    public void TransferRequestType_EnumValues_AreCorrect()
    {
        // Arrange & Act: 获取所有枚举值
        var sendFileRequestValue = (int)TransferRequestType.SendFileRequest;
        var sendFileDataValue = (int)TransferRequestType.SendFileData;
        var cancelTransferValue = (int)TransferRequestType.CancelTransfer;
        
        // Assert: 验证枚举值是否符合预期
        Assert.Equal(0, sendFileRequestValue);
        Assert.Equal(1, sendFileDataValue);
        Assert.Equal(2, cancelTransferValue);
    }
    
    /// <summary>
    /// 测试 TransferRequest 的属性设置器是否正确工作
    /// </summary>
    [Fact]
    public void TransferRequest_PropertySetters_WorkCorrectly()
    {
        // Arrange: 创建初始 TransferRequest 实例
        var request = new TransferRequest
        {
            TransferId = "initial-id",
            Type = TransferRequestType.SendFileRequest,
            FileName = "initial-file.txt",
            FileSize = 1000,
            SenderId = "initial-sender",
            ReceiverId = "initial-receiver"
        };
        
        // Act: 更新所有可更改的属性
        var newTransferId = "updated-id";
        var newType = TransferRequestType.SendFileData;
        var newFileName = "updated-file.txt";
        var newFileSize = 2048;
        var newSenderId = "updated-sender";
        var newReceiverId = "updated-receiver";
        
        // 更新属性
        request.TransferId = newTransferId;
        request.Type = newType;
        request.FileName = newFileName;
        request.FileSize = newFileSize;
        request.SenderId = newSenderId;
        request.ReceiverId = newReceiverId;
        
        // Assert: 验证所有属性都已正确更新
        Assert.Equal(newTransferId, request.TransferId);
        Assert.Equal(newType, request.Type);
        Assert.Equal(newFileName, request.FileName);
        Assert.Equal(newFileSize, request.FileSize);
        Assert.Equal(newSenderId, request.SenderId);
        Assert.Equal(newReceiverId, request.ReceiverId);
    }
    
    /// <summary>
    /// 测试 TransferResponse 的属性设置器是否正确工作
    /// </summary>
    [Fact]
    public void TransferResponse_PropertySetters_WorkCorrectly()
    {
        // Arrange: 创建初始 TransferResponse 实例
        var response = new TransferResponse
        {
            TransferId = "initial-id",
            Accepted = true,
            Message = "Initial message"
        };
        
        // Act: 更新所有可更改的属性
        var newTransferId = "updated-id";
        var newAccepted = false;
        var newMessage = "Updated message";
        
        // 更新属性
        response.TransferId = newTransferId;
        response.Accepted = newAccepted;
        response.Message = newMessage;
        
        // Assert: 验证所有属性都已正确更新
        Assert.Equal(newTransferId, response.TransferId);
        Assert.Equal(newAccepted, response.Accepted);
        Assert.Equal(newMessage, response.Message);
    }
    
    /// <summary>
    /// 测试不同 TransferRequestType 枚举值的赋值是否正确
    /// </summary>
    [Fact]
    public void TransferRequestType_AssignsCorrectly()
    {
        // Arrange: 创建 TransferRequest 实例
        var request = new TransferRequest
        {
            TransferId = "test-id",
            FileName = "test.txt",
            FileSize = 1000,
            SenderId = "sender",
            ReceiverId = "receiver"
        };
        
        // Act: 依次赋值不同的请求类型
        request.Type = TransferRequestType.SendFileRequest;
        var isSendFileRequest = request.Type == TransferRequestType.SendFileRequest;
        
        request.Type = TransferRequestType.SendFileData;
        var isSendFileData = request.Type == TransferRequestType.SendFileData;
        
        request.Type = TransferRequestType.CancelTransfer;
        var isCancelTransfer = request.Type == TransferRequestType.CancelTransfer;
        
        // Assert: 验证请求类型赋值是否正确
        Assert.True(isSendFileRequest);
        Assert.True(isSendFileData);
        Assert.True(isCancelTransfer);
    }
    
    /// <summary>
    /// 测试 TransferResponse 的 Accepted 属性可以正确切换布尔值
    /// </summary>
    [Fact]
    public void TransferResponse_Accepted_ChangesCorrectly()
    {
        // Arrange: 创建 TransferResponse 实例
        var response = new TransferResponse
        {
            TransferId = "test-id",
            Message = "Test message"
        };
        
        // Act: 初始值应为 false
        var initialValue = response.Accepted;
        
        // 设置为 true
        response.Accepted = true;
        var trueValue = response.Accepted;
        
        // 再次设置为 false
        response.Accepted = false;
        var falseValue = response.Accepted;
        
        // Assert: 验证 Accepted 属性可以正确切换布尔值
        Assert.False(initialValue); // 默认值应为 false
        Assert.True(trueValue);
        Assert.False(falseValue);
    }
}