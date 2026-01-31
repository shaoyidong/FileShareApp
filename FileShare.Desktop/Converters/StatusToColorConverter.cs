using FileShare.Core.Models;
using System;
using System.Globalization;

namespace FileShare.Desktop.Converters
{
    public class StatusToColorConverter : Avalonia.Data.Converters.IValueConverter
    {
        public static StatusToColorConverter Instance { get; } = new StatusToColorConverter();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferStatus status)
            {
                return status switch
                {
                    TransferStatus.Completed => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2ecc71")),
                    TransferStatus.Failed => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#e74c3c")),
                    TransferStatus.Transferring => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3498db")),
                    TransferStatus.Pending => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#f39c12")),
                    TransferStatus.Cancelled => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#95a5a6")),
                    _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7f8c8d"))
                };
            }
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7f8c8d"));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}