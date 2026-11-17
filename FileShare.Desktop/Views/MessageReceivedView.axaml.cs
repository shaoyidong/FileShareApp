using Avalonia.Controls;
using Avalonia.Interactivity;
using Shyd.Prism.Localization;
using FileShare.Desktop.ViewModels;
using System;

namespace FileShare.Desktop.Views;

/// <summary>
/// 收到文本消息时的展示内容弹窗（仿 Prism ShowDialog 的 UserControl View）：全文 + 一键复制。
/// <para>
/// 与「文件接收请求」弹窗类似，但文本消息免确认（接收端协议层已直接完成），
/// 因此用非模态窗口且宿主以非激活方式显示（由 VM 配置 ShowActivated=false），
/// 既能让用户看到消息，又不会抢焦点 / 阻塞 UI 线程。
/// 绑定 <see cref="MessageReceivedViewModel"/>，由 DialogService 注入参数；
/// 复制全文需要访问 TopLevel 剪贴板，保留在 code-behind。
/// </para>
/// </summary>
public partial class MessageReceivedView : UserControl
{
    public MessageReceivedView()
    {
        InitializeComponent();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MessageReceivedViewModel viewModel)
        {
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                HintText.Text = Loc.Get("剪贴板不可用");
                HintText.IsVisible = true;
                return;
            }

            await clipboard.SetTextAsync(viewModel.Message);
            HintText.Text = Loc.Get("已复制文本消息");
            HintText.IsVisible = true;
        }
        catch (Exception ex)
        {
            HintText.Text = Loc.F("复制失败: {0}", ex.Message);
            HintText.IsVisible = true;
        }
    }
}
