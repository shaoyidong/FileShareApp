using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.IO;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 发送队列中的一项（可重排）。
/// </summary>
public partial class SendQueueItemViewModel : ViewModelBase
{
    public SendQueueItemViewModel(string path, string? relativePath = null)
    {
        Path = path;
        RelativePath = relativePath;
        DisplayName = relativePath is null
            ? System.IO.Path.GetFileName(path)
            : relativePath.Replace('\\', '/');

        long size = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                size = info.Length;
            }
        }
        catch (Exception)
        {
            // 大小只用于展示
        }

        SizeText = FormatSize(size);
    }

    /// <summary>本地绝对路径。</summary>
    public string Path { get; }

    /// <summary>文件夹传输时的相对路径。</summary>
    public string? RelativePath { get; }

    /// <summary>显示名（文件夹内文件显示相对路径）。</summary>
    public string DisplayName { get; }

    /// <summary>大小文本。</summary>
    public string SizeText { get; }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:F1} {units[index]}";
    }
}
