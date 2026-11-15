using Avalonia.Controls;

namespace FileShare.Desktop.Views;

/// <summary>
/// 「发送文本」内容弹窗视图（仿 Prism ShowDialog 的 UserControl View）：
/// 绑定 <see cref="ViewModels.SendTextViewModel"/>，由 DialogService 放进宿主窗口显示；
/// 发送（Ok）/ 取消（Cancel）由 ViewModel 请求关闭。
/// </summary>
public partial class SendTextView : UserControl
{
    public SendTextView()
    {
        InitializeComponent();
    }
}
