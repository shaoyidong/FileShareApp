using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FileShare.Core.Common;

/// <summary>
/// 文件类型辅助类
/// </summary>
public static class FileTypeHelper
{
    /// <summary>
    /// 根据文件类型获取保存目录
    /// </summary>
    public static string GetDirectoryByFileType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLower();
        string directory;

        // 根据文件扩展名选择合适的目录
        if (IsImageFile(extension))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }
        else if (IsVideoFile(extension))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
        else if (IsAudioFile(extension))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
        else if (IsDocumentFile(extension))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        else
        {
            // 默认使用下载目录
            directory = GetDownloadsPathCrossPlatform();
        }

        return directory;
    }

    // 下载文件夹的GUID标识符[citation:1]
    private static readonly Guid FolderDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
       [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
       uint dwFlags,
       IntPtr hToken,
       out string ppszPath
    );

    public static string GetDownloadsPathWindows()
    {
        string path;
        SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out path);
        return path;
    }

    // 一个综合的跨平台尝试方法
    public static string GetDownloadsPathCrossPlatform()
    {
        // 优先尝试Windows API
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                return GetDownloadsPathWindows();
            }
            catch
            {
                // API调用失败，回退到备用方案
            }
        } 

        // 备用方案：基于用户主目录拼接
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultDownloads = Path.Combine(userProfile, "Downloads");

        // (可选) 可以在此添加macOS或Linux的特定逻辑
        // 例如，读取环境变量或配置文件

        return defaultDownloads;
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