namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 弹窗 ViewModel 的可选契约：在显示前配置宿主窗口属性（仿 Prism 的 Dialog.WindowStyle）。
/// <para>
/// 与 Prism 的 XAML 附加属性不同，这里用类型化配置对象——编译期可查、可单测、AOT 安全，
/// 且与 <see cref="IDialogAware.Title"/> 一样由 ViewModel 统一持有弹窗外观契约。
/// 内容弹窗（UserControl 视图）作用于宿主窗口；窗口弹窗（Window 视图）作用于自身。
/// </para>
/// <para>
/// 由 <see cref="DialogService"/> 在 <see cref="IDialogAware.OnDialogOpened"/> 之后、
/// 显示之前调用一次（因此可以根据打开参数动态决定尺寸等属性）。
/// </para>
/// </summary>
public interface IDialogAwareWindowOptions
{
    /// <summary>配置窗口显示属性；只赋值需要覆盖的项，其余保持默认。</summary>
    void ConfigureWindowOptions(DialogWindowOptions options);
}
