using FileShare.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FileShare.Mobile.Helpers
{
    class TransferDirectionToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferDirection direction)
            {
                return direction switch
                {
                    TransferDirection.Send => Color.FromArgb("#449c12"),
                    TransferDirection.Receive => Color.FromArgb("#3498db"),                    
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
}
