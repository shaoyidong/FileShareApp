using Avalonia.Controls;
using Avalonia.Interactivity;
using FileShare.Desktop.Localization;
using System;

namespace FileShare.Desktop.Views;

/// <summary>
/// 收到文本消息时的展示窗口：全文 + 一键复制。
/// <para>
/// 与「文件接收请求」弹窗类似，但文本消息免确认（接收端协议层已直接完成），
/// 因此用非模态窗口 + ShowActivated=False，既能让用户看到消息，又不会抢焦点 / 阻塞 UI 线程。
/// </para>
/// </summary>
public partial class MessageReceivedWindow : Window
{
    private readonly string _message;

    /// <summary>供 XAML 预览器使用。</summary>
    public MessageReceivedWindow()
        : this(null, string.Empty)
    {
    }

    public MessageReceivedWindow(string? senderName, string? message)
    {
        InitializeComponent();

        _message = message ?? string.Empty;
        SenderText.Text = string.IsNullOrWhiteSpace(senderName)
            ? Loc.Get("收到文本消息")
            : Loc.F("收到来自 {0} 的文本消息", senderName);
        MessageTextBlock.Text = _message;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is null)
            {
                HintText.Text = Loc.Get("剪贴板不可用");
                HintText.IsVisible = true;
                return;
            }

            await Clipboard.SetTextAsync(_message);
            HintText.Text = Loc.Get("已复制文本消息");
            HintText.IsVisible = true;
        }
        catch (Exception ex)
        {
            HintText.Text = Loc.F("复制失败: {0}", ex.Message);
            HintText.IsVisible = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
