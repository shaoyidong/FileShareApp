using Avalonia.Controls;
using System;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>弹窗显示选项。</summary>
public sealed class DialogOptions
{
    /// <summary>指定 owner 窗口；默认自动取主窗口。</summary>
    public Window? Owner { get; init; }

    /// <summary>超时后自动关闭弹窗并回传 <see cref="ButtonResult.Cancel"/>（默认不超时）。</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>内容弹窗（UserControl 视图）使用的宿主窗口名称（需先 AddDialogWindow 注册）；
    /// 缺省用默认宿主（AddDialogWindow 注册的 null 名宿主或内置 <see cref="DialogWindow"/>）。窗口弹窗忽略此项。</summary>
    public string? HostWindow { get; init; }
}
