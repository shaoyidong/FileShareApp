using Avalonia.Controls;
using Avalonia.Input;
using FileShare.Desktop.Localization;
using System;

namespace FileShare.Desktop.Views;

/// <summary>
/// 通知卡片窗口（无边框、置顶、点击回调）。
/// </summary>
public partial class ToastWindow : Window
{
    private readonly Action? _onClick;

    public ToastWindow()
        : this("通知", string.Empty, null)
    {
    }

    public ToastWindow(string title, string message, Action? onClick)
    {
        InitializeComponent();

        _onClick = onClick;
        TitleText.Text = title;
        MessageText.Text = message;

        // 只有带点击回调（会激活主窗口 / 弹出确认窗）的通知才需要「点击查看」引导。
        // 未传回调的纯通知（启动失败、收到文件等）只说明自动消失时间，避免误导用户去点。
        // 时长与 ToastService.DefaultDurationMs 保持一致，调整该常量时需同步这两条文案。
        HintText.Text = onClick is null
            ? Loc.Get("6 秒后自动关闭")
            : Loc.Get("点击查看（6 秒后自动关闭）");

        PointerPressed += OnPointerPressed;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _onClick?.Invoke();
        Close();
    }
}
