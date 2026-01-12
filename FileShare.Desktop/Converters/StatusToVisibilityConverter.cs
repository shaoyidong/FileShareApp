using Avalonia.Data.Converters;
using FileShare.Core.Models;
using System;
using System.Globalization;

namespace FileShare.Desktop.Converters
{
    public class StatusToVisibilityConverter : IValueConverter
    {
        public static readonly StatusToVisibilityConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferStatus status)
            {
                return status == TransferStatus.Pending;
            }
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}