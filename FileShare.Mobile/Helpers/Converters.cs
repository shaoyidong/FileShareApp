using System.Globalization;
using FileShare.Core.Models;

namespace FileShare.Mobile.Helpers;

/// <summary>
/// 传输状态到可见性转换器（仅当状态为Pending时显示）
/// </summary>
public class StatusToVisibilityConverter : IValueConverter
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

/// <summary>
/// 文件大小转换器（用于进度条）
/// </summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long transferredSize && parameter is object item)
        {
            // 通过反射获取FileSize属性
            var fileSizeProperty = item.GetType().GetProperty("FileSize");
            if (fileSizeProperty != null)
            {
                var fileSize = (long)fileSizeProperty.GetValue(item);
                if (fileSize > 0)
                {
                    return (double)transferredSize / fileSize;
                }
            }
        }
        return 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 传输状态到颜色转换器
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TransferStatus status)
        {
            switch (status)
            {
                case TransferStatus.Pending:
                    return Colors.Orange;
                case TransferStatus.Transferring:
                    return Colors.Blue;
                case TransferStatus.Completed:
                    return Colors.Green;
                case TransferStatus.Failed:
                    return Colors.Red;
                case TransferStatus.Cancelled:
                    return Colors.Gray;
                default:
                    return Colors.Black;
            }
        }
        return Colors.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}