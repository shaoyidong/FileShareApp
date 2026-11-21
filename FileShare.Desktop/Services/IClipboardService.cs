using Avalonia;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services;

/// <summary>剪贴板内容的优先级：文件 → 图片 → 文本，与「发送剪贴板」的既有行为一致。</summary>
public enum ClipboardContentKind
{
    None,
    Files,
    Image,
    Text
}

/// <summary>一次剪贴板读取的结果。Image 的 <see cref="ImagePath"/> 是调用方负责删除的临时 PNG。</summary>
public sealed record ClipboardContent(
    ClipboardContentKind Kind,
    IReadOnlyList<string> FilePaths,
    string? Text,
    string? ImagePath)
{
    public static ClipboardContent Empty { get; } =
        new(ClipboardContentKind.None, Array.Empty<string>(), null, null);
}

/// <summary>
/// 系统剪贴板服务：调用方不直接接触 TopLevel/IClipboard，方便平台替换与单元测试。
/// 实现必须在 UI 线程调用（内部按需解析当前 TopLevel 或主窗口的剪贴板）。
/// </summary>
public interface IClipboardService
{
    Task<bool> SetTextAsync(string text, Visual? visual = null);

    /// <returns>null 表示剪贴板不可用；否则返回当前可发送内容的快照。</returns>
    Task<ClipboardContent?> ReadAsync(Visual? visual = null);
}

/// <summary>设计器 / 单元测试使用的空实现。</summary>
internal sealed class NullClipboardService : IClipboardService
{
    public static readonly NullClipboardService Instance = new();

    public Task<bool> SetTextAsync(string text, Visual? visual = null) => Task.FromResult(false);

    public Task<ClipboardContent?> ReadAsync(Visual? visual = null) =>
        Task.FromResult<ClipboardContent?>(null);
}