using Avalonia.Controls;

namespace FileShare.Desktop.Views;

/// <summary>
/// 接收文件的模态确认内容弹窗（P0：原来只在任务列表里内联两个按钮，用户很容易错过 60 秒确认窗口）。
/// 绑定 <see cref="ViewModels.ReceiveRequestViewModel"/>，由 DialogService 放进 DialogWindow 宿主
/// 显示并注入参数；接收（Ok）/ 拒绝（No）由 ViewModel 请求关闭，结果携带 ReceiveRequestResult。
/// </summary>
public partial class ReceiveRequestView : UserControl
{
    public ReceiveRequestView()
    {
        InitializeComponent();
    }
}
