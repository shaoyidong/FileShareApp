using System.Globalization;
using FileShare.Core.Models;
using Microsoft.Maui.Converters;
using Microsoft.Maui.Graphics;

namespace FileShare.Mobile.Helpers;

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TransferStatus status)
        {
            return status switch
            {
                TransferStatus.Completed => Color.FromArgb("#2ecc71"),
                TransferStatus.Failed => Color.FromArgb("#e74c3c"),
                TransferStatus.Transferring => Color.FromArgb("#3498db"),
                TransferStatus.Pending => Color.FromArgb("#f39c12"),
                TransferStatus.Cancelled => Color.FromArgb("#95a5a6"),
                _ => Color.FromArgb("#7f8c8d")
            };
        }
        return Color.FromArgb("#7f8c8d");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
