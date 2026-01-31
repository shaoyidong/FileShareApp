using Avalonia.Data.Converters;
using FileShare.Core.Models;
using FileShare.Desktop.ViewModels;
using System;
using System.Globalization;

namespace FileShare.Desktop.Converters
{
    public class TransferVisibilityConverter : IValueConverter
    {
        public static readonly TransferVisibilityConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is FileTransferViewModel viewModel)
            {
                return viewModel.Direction == TransferDirection.Receive && viewModel.Status == TransferStatus.Pending;
            }
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}