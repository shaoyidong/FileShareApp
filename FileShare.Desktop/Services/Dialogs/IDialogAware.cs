using System;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 弹窗 ViewModel 契约（仿 Prism 的 IDialogAware）。
/// <para>
/// 实现 <see cref="IDialogAware"/> 的 ViewModel 在 Program 中通过
/// <c>services.AddDialog&lt;TView, TViewModel&gt;()</c> 注册后，即可由
/// <see cref="IDialogService"/> 统一打开：服务负责注入参数、订阅
/// <see cref="RequestClose"/>、显示窗口并在关闭时回传结果。
/// </para>
/// </summary>
public interface IDialogAware
{
    /// <summary>弹窗标题（窗口 XAML 里已有 loc:Tr 绑定时，以 XAML 为准，服务不会覆盖）。</summary>
    string Title { get; }

    /// <summary>ViewModel 主动请求关闭弹窗时触发；DialogService 负责关闭窗口并把结果回传给调用方。</summary>
    event Action<IDialogResult>? RequestClose;

    /// <summary>是否允许关闭（窗口 Closing 事件里用于拒绝关闭请求）。</summary>
    bool CanCloseDialog();

    /// <summary>弹窗打开时由 DialogService 调用，用于接收参数（在窗口显示之前）。</summary>
    void OnDialogOpened(IDialogParameters parameters);

    /// <summary>弹窗关闭后由 DialogService 调用（无论以何种方式关闭），用于释放资源。</summary>
    void OnDialogClosed();
}
