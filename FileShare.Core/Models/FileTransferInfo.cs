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

    /// <summary>
    /// 实际落盘的文件名。为空表示与 <see cref="FileName"/> 相同；
    /// 收到同名文件时接收端会按策略自动重命名，这里记录最终名字，历史记录以它为准。
    /// </summary>
    public string? SavedFileName { get; set; }

    /// <summary>
    /// 文件夹传输时的相对路径（如 <c>照片/2024/a.jpg</c>）。单文件传输时为 <c>null</c>。
    /// </summary>
    public string? RelativePath { get; set; }

    /// <summary>
    /// 本机上的绝对文件路径：发送时是源文件，接收完成时是落盘文件。
    /// 历史记录用它来「打开文件 / 在资源管理器中显示」，比 SavePath + FileName 拼接可靠
    /// （发送与接收的目录语义不同）。
    /// </summary>
    public string? FilePath { get; set; }

    public TransferDirection Direction { get; set; }

    /// <summary>
    /// 传输开始时间（UTC），首次进度采样时设置
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// 瞬时传输速率（字节/秒），基于最近一次采样计算
    /// </summary>
    public long TransferRateBytesPerSec { get; set; }

    /// <summary>
    /// 平均传输速率（字节/秒），基于StartTime到当前的总传输量计算
    /// </summary>
    public long AverageRateBytesPerSec
    {
        get
        {
            if (StartTime == null || TransferredSize <= 0) return 0;
            var elapsed = (DateTime.UtcNow - StartTime.Value).TotalSeconds;
            return elapsed > 0 ? (long)(TransferredSize / elapsed) : 0;
        }
    }

    public byte[]? ReceivedHash { get; set; }
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
    Cancelled,   // 取消
    WaitingChecksum,  // 数据已收完，等待校验和
}

public enum TransferDirection
{
    Send,     // 发送
    Receive   // 接收
}
