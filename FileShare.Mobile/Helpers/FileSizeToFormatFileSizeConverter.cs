using FileShare.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FileShare.Mobile.Helpers
{
    public class FileSizeToFormatFileSizeConverter : IValueConverter
    {
        public static readonly FileSizeToFormatFileSizeConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is long fileSize)
            {
                return FormatFileSize(fileSize);
            }
            return -1;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private string FormatFileSize(long size)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int index = 0;
            double sizeDouble = size;

            while (sizeDouble >= 1024 && index < suffixes.Length - 1)
            {
                sizeDouble /= 1024;
                index++;
            }

            return $"{sizeDouble:F1}{suffixes[index]}";
        }
    }
}
