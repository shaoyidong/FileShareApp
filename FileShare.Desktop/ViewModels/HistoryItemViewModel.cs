using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using System;
using System.IO;

namespace FileShare.Desktop.ViewModels;


/// <summary>
/// 历史列表条目的展示模型。
/// <para>
/// <see cref="ReceiveHistoryEntity"/> 只承载数据库字段；
/// 所有「数据库里没有、仅用于界面显示」的派生属性（本地时间、标题、染色标记等）
/// 都集中在这里，视图与 ViewModel 之间不直接传递实体。
/// </para>
/// </summary>
public sealed class HistoryItemViewModel
{
    public int Id { get; init; }

    /// <summary>文件名（文本消息记录里是消息摘要）。</summary>
    public required string FileName { get; init; }

    public long FileSize { get; init; }

    /// <summary>本机绝对路径（接收 = 落盘位置；发送 = 源文件；失败接收可能为空）。</summary>
    public string? FilePath { get; init; }

    /// <summary>兼容旧记录的目录字段。</summary>
    public required string SavePath { get; init; }

    public TransferStatus Status { get; init; }

    public TransferDirection Direction { get; init; }

    /// <summary>失败/取消原因。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>文本消息全文（非空即消息记录）。</summary>
    public string? Message { get; init; }

    /// <summary>入库时间（UTC）。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>对端显示名。</summary>
    public required string PeerDisplayName { get; init; }

    /// <summary>本地时间（数据库读回的 Kind 为 Unspecified，按 UTC 换算）。</summary>
    public DateTime CreatedAtLocal => DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc).ToLocalTime();

    public bool IsMessage => !string.IsNullOrEmpty(Message);

    /// <summary>普通文件记录（UI 用来显示「打开文件」类操作）。</summary>
    public bool IsFileRecord => !IsMessage;

    public bool IsSuccess => Status == TransferStatus.Completed;

    /// <summary>结果的中文描述。</summary>
    public string StatusText => Status switch
    {
        TransferStatus.Completed => "成功",
        TransferStatus.Failed => "失败",
        TransferStatus.Cancelled => "已取消",
        _ => "未知"
    };

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>消息摘要（避免长文本撑破卡片）。</summary>
    public string MessagePreview
    {
        get
        {
            if (string.IsNullOrEmpty(Message))
            {
                return string.Empty;
            }

            var singleLine = Message.Replace('\r', ' ').Replace('\n', ' ');
            return singleLine.Length > 100 ? singleLine[..100] + "…" : singleLine;
        }
    }

    /// <summary>
    /// 历史卡片唯一的主标题：文本消息显示正文预览，文件记录显示文件名。
    /// <para>文本消息入库时 FileName 已是同一段正文的摘要，不再另起一行显示 MessagePreview。</para>
    /// </summary>
    public string DisplayTitle => IsMessage ? MessagePreview : FileName;

    /// <summary>是否有可打开的本地文件。</summary>
    public bool HasFile => !IsMessage && ResolveLocalFile() is not null;

    /// <summary>
    /// 解析记录对应的本机文件路径：优先绝对路径 <see cref="FilePath"/>，
    /// 旧记录退回 <see cref="SavePath"/> + <see cref="FileName"/>；文件不存在时返回 null。
    /// </summary>
    public string? ResolveLocalFile()
    {
        if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
        {
            return FilePath;
        }

        if (string.IsNullOrWhiteSpace(SavePath))
        {
            return null;
        }

        var legacy = Path.Combine(SavePath, FileName);
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>从数据库实体构建展示模型。</summary>
    public static HistoryItemViewModel FromEntity(ReceiveHistoryEntity entity) => new()
    {
        Id = entity.Id,
        FileName = entity.FileName,
        FileSize = entity.FileSize,
        FilePath = entity.FilePath,
        SavePath = entity.SavePath,
        Status = entity.Status,
        Direction = entity.Direction,
        ErrorMessage = entity.ErrorMessage,
        Message = entity.Message,
        CreatedAt = entity.CreatedAt,
        PeerDisplayName = entity.PeerName ?? entity.SenderDeviceName ?? entity.PeerId ?? entity.SenderId
    };
}
