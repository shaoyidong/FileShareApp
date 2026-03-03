using FileShare.Core.Common;
using FileShare.Core.Models.Entities;
using System;
using System.Globalization;
using System.IO;
using Avalonia.Media.Imaging;
using FileShare.Desktop.Assets.Fonts;

namespace FileShare.Desktop.Converters
{
    public class FileTypeToImageSourceConverter : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly FileTypeToImageSourceConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string param = parameter as string ?? string.Empty;

            if (value is ReceiveHistoryEntity history)
            {
                string fullPath = Path.Combine(history.SavePath, history.FileName);
                string extension = Path.GetExtension(history.FileName).ToLower();
                bool isImageFile = FileTypeHelper.IsImageFile(extension);
                bool fileExists = File.Exists(fullPath);

                switch (param)
                {
                    case "isImage":
                        return isImageFile && fileExists;
                    case "isIcon":
                        return !isImageFile || !fileExists;
                    default:
                        if (isImageFile && fileExists)
                        {
                            try
                            {
                                return new Bitmap(fullPath);
                            }
                            catch
                            {
                                return GetFileIconKey(extension);
                            }
                        }
                        else
                        {
                            return GetFileIconKey(extension);
                        }
                }
            }

            switch (param)
            {
                case "isImage":
                    return false;
                case "isIcon":
                    return true;
                default:
                    return "document_24_regular";
            }
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private string GetFileIconKey(string extension)
        {
            if (FileTypeHelper.IsImageFile(extension))
            {
                return FluentUI.image_24_regular;
            }
            else if (FileTypeHelper.IsVideoFile(extension))
            {
                return FluentUI.video_24_regular;
            }
            else if (FileTypeHelper.IsAudioFile(extension))
            {
                return FluentUI.music_note_1_24_regular;
            }
            else if (FileTypeHelper.IsDocumentFile(extension))
            {
                return FluentUI.document_24_regular;
            }
            else if (FileTypeHelper.IsCompressedFile(extension))
            {
                return FluentUI.folder_zip_24_regular;
            }            
            else
            {
                return FluentUI.document_24_regular;
            }
        }
    }
}