using System.Globalization;
using FileShare.Core.Models;

namespace FileShare.Mobile.Helpers;
public class StatusToPendingVisibilityConverter : IValueConverter
{
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
