using FileShare.Core.Common;
using FileShare.Core.Models.Entities;
using System;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FileShare.Desktop.Assets.Fonts;

namespace FileShare.Desktop.Converters
{
    /// <summary>
    /// 历史记录文件类型 → 缩略图 / 图标 / 显隐。
    /// <para>
    /// 必须按用途区分，否则把图标字形字符串绑到 <c>Image.Source</c> 会报
    /// “Could not convert ... (System.String) to Avalonia.Media.IImage”：
    /// <list type="bullet">
    /// <item><c>isImage</c> / <c>isIcon</c> → bool，用于 IsVisible</item>
    /// <item><c>image</c> → IImage 或 null，只给 Image.Source</item>
    /// <item><c>icon</c> → string，只给 TextBlock.Text</item>
    /// </list>
    /// </para>
    /// </summary>
    public class FileTypeToImageSourceConverter : Avalonia.Data.Converters.IValueConverter
    {
        private static readonly string FallbackIcon = FluentUI.document_48_regular;

        public static readonly FileTypeToImageSourceConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var param = parameter as string ?? string.Empty;

            if (value is ReceiveHistoryEntity history)
            {
                var fullPath = !string.IsNullOrEmpty(history.FilePath)
                    ? history.FilePath!
                    : Path.Combine(history.SavePath, history.FileName);

                var extension = Path.GetExtension(history.FileName).ToLowerInvariant();
                var isImageFile = FileTypeHelper.IsImageFile(extension);
                var fileExists = !string.IsNullOrEmpty(fullPath) && File.Exists(fullPath);

                return param switch
                {
                    "isImage" => isImageFile && fileExists,
                    "isIcon" => !isImageFile || !fileExists,
                    "icon" => GetFileIcon(extension),
                    "image" => TryLoadBitmap(isImageFile, fileExists, fullPath),
                    _ => null,
                };
            }

            return param switch
            {
                "isImage" => false,
                "isIcon" => true,
                "icon" => FallbackIcon,
                _ => null,
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        /// <summary>只有真正是图片且文件存在时才返回 Bitmap，其余一律 null（Image.Source 不接受字符串）。</summary>
        private static IImage? TryLoadBitmap(bool isImageFile, bool fileExists, string fullPath)
        {
            if (!isImageFile || !fileExists)
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(fullPath);
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                // 文件被占用/损坏时退回图标显示
                return null;
            }
        }

        private static string GetFileIcon(string extension)
        {
            if (FileTypeHelper.IsImageFile(extension))
            {
                return FluentUI.image_48_regular;
            }

            if (FileTypeHelper.IsVideoFile(extension))
            {
                return FluentUI.video_48_regular;
            }

            if (FileTypeHelper.IsAudioFile(extension))
            {
                return FluentUI.music_note_1_24_regular;
            }

            if (FileTypeHelper.IsDocumentFile(extension))
            {
                return FluentUI.document_48_regular;
            }

            if (FileTypeHelper.IsCompressedFile(extension))
            {
                return FluentUI.folder_zip_24_regular;
            }

            return FluentUI.document_48_regular;
        }
    }
}
