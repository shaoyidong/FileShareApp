namespace FileShare.Core.Models;

/// <summary>
/// 传输状态枚举
/// </summary>
public enum TransferStatus
{
    Pending,    // 等待中
    Transferring, // 传输中
    Completed,  // 完成
    Failed,     // 失败
    Cancelled,   // 取消
    WaitingChecksum,  // 数据已收完，等待校验和
}
