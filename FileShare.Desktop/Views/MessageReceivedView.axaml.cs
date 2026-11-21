using Avalonia.Controls;
using FileShare.Desktop.ViewModels;

namespace FileShare.Desktop.Views;

/// <summary>
/// 收到文本消息时的展示内容弹窗（仿 Prism ShowDialog 的 UserControl View）。
/// 复制全文与关闭都由 <see cref="MessageReceivedViewModel"/> 的命令处理，视图只保留内容与按钮栏。
/// </summary>
public partial class MessageReceivedView : UserControl
{
    public MessageReceivedView()
    {
        InitializeComponent();
    }
}