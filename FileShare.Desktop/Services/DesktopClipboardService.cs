using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services;

/// <summary>
/// 桌面剪贴板实现：视觉树传入时优先用该 TopLevel 的剪贴板（如消息弹窗），
/// 否则用主窗口的剪贴板；读取优先级保持 文件 → 图片 → 文本。
/// </summary>
public sealed class DesktopClipboardService : IClipboardService
{
    private readonly IClassicDesktopStyleApplicationLifetime _appLifetime;
    private readonly ILogger<DesktopClipboardService> _logger;

    public DesktopClipboardService(
        IClassicDesktopStyleApplicationLifetime appLifetime,
        ILogger<DesktopClipboardService>? logger = null)
    {
        _appLifetime = appLifetime;
        _logger = logger ?? NullLogger<DesktopClipboardService>.Instance;
    }

    public async Task<bool> SetTextAsync(string text, Visual? visual = null)
    {
        var clipboard = ResolveClipboard(visual);
        if (clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
        return true;
    }

    public async Task<ClipboardContent?> ReadAsync(Visual? visual = null)
    {
        var clipboard = ResolveClipboard(visual);
        if (clipboard is null)
        {
            return null;
        }

        var paths = await ReadFilePathsAsync(clipboard).ConfigureAwait(true);
        if (paths.Count > 0)
        {
            return new ClipboardContent(ClipboardContentKind.Files, paths, null, null);
        }

        var imagePath = await SaveImageToTempFileAsync(clipboard).ConfigureAwait(true);
        if (imagePath is not null)
        {
            return new ClipboardContent(ClipboardContentKind.Image, Array.Empty<string>(), null, imagePath);
        }

        var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        return string.IsNullOrWhiteSpace(text)
            ? ClipboardContent.Empty
            : new ClipboardContent(ClipboardContentKind.Text, Array.Empty<string>(), text, null);
    }

    private IClipboard? ResolveClipboard(Visual? visual)
    {
        if (visual is not null)
        {
            return TopLevel.GetTopLevel(visual)?.Clipboard ?? _appLifetime.MainWindow?.Clipboard;
        }

        // VM 命令没有 Visual：优先取当前活动窗口（可能是消息弹窗宿主），否则回退主窗口
        var activeWindow = _appLifetime.Windows.FirstOrDefault(window => window.IsActive);
        return activeWindow?.Clipboard ?? _appLifetime.MainWindow?.Clipboard;
    }

    private async Task<IReadOnlyList<string>> ReadFilePathsAsync(IClipboard clipboard)
    {
        var paths = new List<string>();

        void AddPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (File.Exists(path))
            {
                paths.Add(path);
            }
            else if (Directory.Exists(path))
            {
                paths.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
            }
        }

        try
        {
            var items = await clipboard.TryGetFilesAsync().ConfigureAwait(true);
            if (items is { Length: > 0 })
            {
                foreach (var item in items)
                {
                    AddPath(item.Path.IsAbsoluteUri ? item.Path.LocalPath : item.Path.OriginalString);
                }
            }
        }
        catch (Exception ex)
        {
            // 平台未实现该格式时走下面的兜底
            _logger.LogDebug(ex, "读取剪贴板文件列表失败");
        }

        if (paths.Count == 0)
        {
            try
            {
                var transfer = await clipboard.TryGetDataAsync().ConfigureAwait(true);
                if (transfer is not null)
                {
                    var storageItems = await transfer.TryGetValuesAsync(DataFormat.File).ConfigureAwait(true);
                    if (storageItems is { Length: > 0 })
                    {
                        foreach (var item in storageItems)
                        {
                            AddPath(item.Path.IsAbsoluteUri ? item.Path.LocalPath : item.Path.OriginalString);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 剪贴板里可能只有文本 / 图片
                _logger.LogDebug(ex, "读取剪贴板 DataFormat.File 失败");
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<string?> SaveImageToTempFileAsync(IClipboard clipboard)
    {
        Bitmap? bitmap;
        try
        {
            bitmap = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取剪贴板位图失败");
            return null;
        }

        if (bitmap is null)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "FileShare.Clipboard");
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory,
                $"剪贴板_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N[..6]}.png");
            bitmap.Save(filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存剪贴板图片失败");
            return null;
        }
        finally
        {
            bitmap.Dispose();
        }
    }
}