using FileShare.Core.Models;

namespace FileShare.LocalSend;

/// <summary>
/// 外部协议（LocalSend / 网页分享）的一条传输在 UI 传输列表中的状态快照。
/// <para><c>IsMessage</c> = true 时这是一条原生文本消息：<c>FileName</c> 只是摘要，全文在 <c>MessageText</c>。</para>
/// </summary>
public sealed record ExternalTransferUpdate(
    string TransferId,
    string FileName,
    long FileSize,
    long TransferredSize,
    TransferDirection Direction,
    TransferStatus Status,
    string? SenderId = null,
    string? SavePath = null,
    string? MessageText = null,
    bool IsMessage = false)
{
    public static ExternalTransferUpdate For(
        string transferId, string fileName, long fileSize,
        TransferDirection direction, TransferStatus status,
        long transferredSize = 0, string? senderId = null, string? savePath = null) =>
        new(transferId, fileName, fileSize, transferredSize, direction, status, senderId, savePath);

    /// <summary>构造一条文本消息快照（免确认、不落盘，直接显示全文 + 复制按钮）。</summary>
    public static ExternalTransferUpdate ForMessage(
        string transferId, string summary, string message, long size,
        TransferDirection direction, TransferStatus status,
        string? senderId = null) =>
        new(transferId, summary, size, size, direction, status, senderId, null, message, true);
}
