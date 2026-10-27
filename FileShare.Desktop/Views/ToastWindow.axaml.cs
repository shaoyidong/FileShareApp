using Avalonia.Controls;
using Avalonia.Input;
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

        PointerPressed += OnPointerPressed;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _onClick?.Invoke();
        Close();
    }
}
