using System;
using System.IO;
using FileShare.Core.Services;

namespace FileShare.Core.Common;

/// <summary>
/// 文件类型辅助类
/// </summary>
public static class FileTypeHelper
{
    /// <summary>
    /// 根据文件类型获取保存目录
    /// </summary>
    public static string GetDirectoryByFileType(string fileName, IPlatformDirectoryService directoryService)
    {
        var extension = Path.GetExtension(fileName).ToLower();
        string directory;

        // 根据文件扩展名选择合适的目录
        if (IsImageFile(extension))
        {
            directory = directoryService.GetPicturesDirectory();
        }
        else if (IsVideoFile(extension))
        {
            directory = directoryService.GetVideosDirectory();
        }
        else if (IsAudioFile(extension))
        {
            directory = directoryService.GetMusicDirectory();
        }
        else if (IsDocumentFile(extension))
        {
            directory = directoryService.GetDocumentsDirectory();
        }
        else
        {
            // 默认使用下载目录
            directory = directoryService.GetDownloadsDirectory();
        }

        return directory;
    }

    /// <summary>
    /// 检查是否为图片文件
    /// </summary>
    public static bool IsImageFile(string extension)
    {
        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".svg", ".webp" };
        return imageExtensions.Contains(extension);
    }

    /// <summary>
    /// 检查是否为视频文件
    /// </summary>
    public static bool IsVideoFile(string extension)
    {
        var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".wmv", ".flv", ".mkv", ".webm" };
        return videoExtensions.Contains(extension);
    }

    /// <summary>
    /// 检查是否为音频文件
    /// </summary>
    public static bool IsAudioFile(string extension)
    {
        var audioExtensions = new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma" };
        return audioExtensions.Contains(extension);
    }

    /// <summary>
    /// 检查是否为文档文件
    /// </summary>
    public static bool IsDocumentFile(string extension)
    {
        var documentExtensions = new[] { ".txt", ".doc", ".docx", ".pdf", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp" };
        return documentExtensions.Contains(extension);
    }

    /// <summary>
    /// 检查是否为压缩文件
    /// </summary>
    public static bool IsCompressedFile(string extension)
    {
        var compressedExtensions = new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" };
        return compressedExtensions.Contains(extension);
    }

    /// <summary>
    /// 检查是否为安装文件
    /// </summary>
    public static bool IsInstallerFile(string extension)
    {
        var installerExtensions = new[] { ".exe", ".msi", ".appx", ".dmg", ".pkg" };
        return installerExtensions.Contains(extension);
    }
}