using Shyd.Prism.Notifications;

namespace FileShare.Desktop.Views;

/// <summary>
/// 皮肤主题化的通知卡片窗口：继承框架的 <see cref="ToastWindowBase"/>——
/// 内容注入（ToastTitle / ToastMessage / ToastHint）与点击行为（OnClick → 延迟关窗）都在基类，
/// 本类只负责外观：DynamicResource 绑定皮肤 Fs* 资源（FsSurfaceBrush / FsAccentBrush /
/// FsTextBrush），随皮肤切换即时换肤，替代框架内置的自包含样式 ToastWindow。
/// <para>在 Program.cs 注册 IToastService 时通过 windowFactory 提供本窗口。</para>
/// </summary>
public partial class AppToastWindow : ToastWindowBase
{
    public AppToastWindow()
    {
        InitializeComponent();
    }
}
