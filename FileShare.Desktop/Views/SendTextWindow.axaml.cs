using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileShare.Desktop.Views;

/// <summary>
/// 「发送文本」编辑窗口，返回值是用户输入的文本（取消时为 null）。
/// </summary>
public partial class SendTextWindow : Window
{
    public SendTextWindow()
        : this(null)
    {
    }

    public SendTextWindow(string? targetDeviceName)
    {
        InitializeComponent();

        TargetText.Text = string.IsNullOrWhiteSpace(targetDeviceName)
            ? "接收方：未选择设备"
            : $"接收方：{targetDeviceName}";
    }

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        var text = ContentBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            Close(null);
            return;
        }

        Close(text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
