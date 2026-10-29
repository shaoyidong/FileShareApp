using Avalonia.Data.Converters;
using FileShare.Core.Models;
using FileShare.Desktop.Localization;
using System;
using System.Globalization;

namespace FileShare.Desktop.Converters;

/// <summary>
/// 把传输方向/状态枚举转成本地化文本（历史列表用）。
/// </summary>
public sealed class TransferEnumToTextConverter : IValueConverter
{
    public static readonly TransferEnumToTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var purpose = parameter as string ?? "status";

        if (value is TransferDirection direction)
        {
            return direction == TransferDirection.Send ? Loc.Get("发送") : Loc.Get("接收");
        }

        if (value is TransferStatus status)
        {
            return status switch
            {
                TransferStatus.Completed => Loc.Get("已完成"),
                TransferStatus.Failed => Loc.Get("失败"),
                TransferStatus.Cancelled => Loc.Get("已取消"),
                TransferStatus.Transferring => Loc.Get("传输中"),
                TransferStatus.Pending => Loc.Get("等待中"),
                _ => Loc.Get("未知状态")
            };
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
