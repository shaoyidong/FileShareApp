namespace FileShare.Core.Models;

/// <summary>
/// 文件传输信息
/// </summary>
public class FileTransferInfo
{
    /// <summary>
    /// 传输ID
    /// </summary>
    public required string TransferId { get; set; }
    
    /// <summary>
    /// 文件名
    /// </summary>
    public required string FileName { get; set; }
    
    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public required long FileSize { get; set; }
    
    /// <summary>
    /// 已传输大小（字节）
    /// </summary>
    public long TransferredSize { get; set; }
    
    /// <summary>
    /// 传输状态
    /// </summary>
    public TransferStatus Status { get; set; }
    
    /// <summary>
    /// 发送方ID
    /// </summary>
    public required string SenderId { get; set; }
    
    /// <summary>
    /// 接收方ID
    /// </summary>
    public required string ReceiverId { get; set; }
    
    /// <summary>
    /// 传输进度百分比
    /// </summary>
    public double ProgressPercentage => FileSize > 0 ? (double)TransferredSize / FileSize * 100 : 0;
    
    /// <summary>
    /// 文件保存路径
    /// </summary>
    public string? SavePath { get; set; }

    public TransferDirection Direction { get; set; }
}

/// <summary>
/// 传输状态枚举
/// </summary>
public enum TransferStatus
{
    Pending,    // 等待中
    Transferring, // 传输中
    Completed,  // 完成
    Failed,     // 失败
    Cancelled   // 取消
}

public enum TransferDirection
{
    Send,     // 发送
    Receive   // 接收
}
