using FileShare.Core.Models;
using System;
using System.Globalization;

namespace FileShare.Desktop.Converters
{
    public class TransferDirectionToColorConverter : Avalonia.Data.Converters.IValueConverter
    {
        public static TransferDirectionToColorConverter Instance { get; } = new TransferDirectionToColorConverter();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferDirection direction)
            {
                return direction switch
                {
                    TransferDirection.Send => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#449c12")),
                    TransferDirection.Receive => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3498db")),
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