using Avalonia.Controls;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 内容弹窗的默认宿主窗口（仿 Prism 的 DialogWindow）：
/// <c>AddDialog&lt;TView, TViewModel&gt;()</c> 注册的 UserControl 视图由
/// <see cref="DialogService"/> 放进本窗口显示，Title 取自 ViewModel 的 IDialogAware.Title。
/// <para>自定义 chrome 可继承本类（或任意 Window）后用 <c>services.AddDialogWindow&lt;TWindow&gt;()</c>
/// 注册；内容弹窗通过 <see cref="DialogOptions.HostWindow"/> 选择命名宿主。</para>
/// </summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        Title = "Dialog";
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
    }
}
