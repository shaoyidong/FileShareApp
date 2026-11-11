using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using System.IO;

namespace FileShare.Mobile.ViewModels;

/// <summary>
/// 历史列表条目的展示模型（与桌面端同构）：
/// <see cref="ReceiveHistoryEntity"/> 只承载数据库字段，界面显示用的派生属性集中在这里。
/// </summary>
public sealed class HistoryItemViewModel
{
    public int Id { get; init; }

    /// <summary>文件名（文本消息记录里是消息摘要）。</summary>
    public required string FileName { get; init; }

    public long FileSize { get; init; }

    public string? FilePath { get; init; }

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
    public string PeerDisplayName { get; init; } = string.Empty;

    /// <summary>对端设备名（信息弹窗显示用）。</summary>
    public string? SenderDeviceName { get; init; }

    /// <summary>本地时间（数据库读回的 Kind 为 Unspecified，按 UTC 换算）。</summary>
    public DateTime CreatedAtLocal => DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc).ToLocalTime();

    public bool IsMessage => !string.IsNullOrEmpty(Message);

    public bool IsSuccess => Status == TransferStatus.Completed;

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

    /// <summary>卡片主标题：文本消息显示正文预览，文件记录显示文件名。</summary>
    public string DisplayTitle => IsMessage ? MessagePreview : FileName;

    /// <summary>是否有可打开的本地文件。</summary>
    public bool HasFile => !IsMessage && ResolveLocalFile() is not null;

    /// <summary>
    /// 解析记录对应的本机文件路径：优先绝对路径，旧记录退回 SavePath + FileName；不存在返回 null。
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
        PeerDisplayName = entity.PeerName ?? entity.SenderDeviceName ?? entity.PeerId ?? entity.SenderId,
        SenderDeviceName = entity.SenderDeviceName
    };
}
