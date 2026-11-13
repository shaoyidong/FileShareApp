using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System.Collections.Generic;

namespace FileShare.Mobile.Helpers;

/// <summary>
/// 「发送媒体」用文件类型过滤器：图片 + 视频混合，系统选择器只显示照片和视频，
/// 类似 LocalSend 移动端的「发送媒体」入口，方便快速挑选素材。
/// </summary>
public static class MediaFileTypes
{
    public static readonly FilePickerFileType ImagesAndVideos = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, new[] { "image/*", "video/*" } },
            { DevicePlatform.iOS, new[] { "public.image", "public.movie" } },
            { DevicePlatform.macOS, new[] { "public.image", "public.movie" } },
            { DevicePlatform.MacCatalyst, new[] { "public.image", "public.movie" } },
            { DevicePlatform.WinUI, new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv" } },
            { DevicePlatform.Tizen, new[] { "image/*", "video/*" } },
        });
}
