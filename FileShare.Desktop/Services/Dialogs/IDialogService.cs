using Avalonia.Controls;
using System;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 仿 Prism DialogService 的弹窗服务：统一注册、创建、传参与结果回传。
/// <para>
/// 弹窗 ViewModel 实现 <see cref="IDialogAware"/> 并在 Program 中以
/// <c>services.AddDialog&lt;TView, TViewModel&gt;()</c> 注册；本服务负责解析
/// ViewModel 与视图、注入参数、显示（模态 / 非模态）、超时与关闭回传。
/// 支持两种视图形态：UserControl 内容弹窗（放入宿主窗口，仿 Prism ShowDialog，
/// 宿主由 <c>AddDialogWindow</c> 注册或用内置 <see cref="DialogWindow"/>）、
/// 以及自带 chrome 的 Window 弹窗（行为同 Prism.Avalonia 的 ShowDialogWindow / ShowWindow）。
/// 每个方法都同时提供按名称（非泛型，仿 Prism 的 ShowDialog("DialogView")）与
/// 按 ViewModel 类型（泛型，编译期安全）两种查找方式。
/// </para>
/// </summary>
public interface IDialogService
{
    /// <summary>以回调方式打开模态弹窗（Prism 风格，按名称查找注册）。</summary>
    void ShowDialog(string name, IDialogParameters? parameters, Action<IDialogResult> callback);

    /// <summary>以回调方式打开模态弹窗（Prism 风格，按 ViewModel 类型查找注册）。</summary>
    void ShowDialog<TViewModel>(IDialogParameters? parameters, Action<IDialogResult> callback)
        where TViewModel : class, IDialogAware;

    /// <summary>
    /// 打开模态弹窗并异步等待结果；用户点标题栏直接关闭时回传 <see cref="ButtonResult.Cancel"/>。
    /// 非UI线程调用时内部自动封送到UI线程。
    /// </summary>
    Task<IDialogResult> ShowDialogAsync(string name, IDialogParameters? parameters = null, DialogOptions? options = null);

    /// <summary>打开模态弹窗并异步等待结果，按 ViewModel 类型查找注册。</summary>
    Task<IDialogResult> ShowDialogAsync<TViewModel>(IDialogParameters? parameters = null, DialogOptions? options = null)
        where TViewModel : class, IDialogAware;

    /// <summary>
    /// 打开非模态弹窗（owner 不可见时自动独立显示）。
    /// <para>返回被显示（或激活）的窗口引用，供调用方后续 Activate（如通知卡片点击唤起）。</para>
    /// <para>必须在 UI 线程调用。</para>
    /// </summary>
    Window? Show<TViewModel>(IDialogParameters? parameters = null, Action<IDialogResult>? closedCallback = null)
        where TViewModel : class, IDialogAware;

    /// <summary>按名称打开非模态弹窗。必须在 UI 线程调用。</summary>
    void Show(string name, IDialogParameters? parameters = null, Action<IDialogResult>? closedCallback = null);
}
