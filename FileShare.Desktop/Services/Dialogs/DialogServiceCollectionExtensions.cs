using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Diagnostics.CodeAnalysis;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 仿 Prism 的 <c>IContainerRegistry.RegisterDialog&lt;TView, TViewModel&gt;()</c> /
/// <c>RegisterDialogWindow&lt;TWindow&gt;()</c>：
/// 用显式泛型注册弹窗与宿主窗口，不用字符串查类型 / 程序集扫描，NativeAOT 安全。
/// </summary>
public static class DialogServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个弹窗：视图 <typeparamref name="TView"/> 与 ViewModel <typeparamref name="TViewModel"/>。
    /// <para>
    /// <typeparamref name="TView"/> 为 <see cref="UserControl"/> 时是内容弹窗（仿 Prism ShowDialog）：
    /// 视图由服务放入宿主窗口（默认 <see cref="DialogWindow"/>，可用 <c>AddDialogWindow</c> 注册自定义 chrome）；
    /// 为 <see cref="Window"/> 时是窗口弹窗（自带 chrome，行为同 ShowDialogWindow / ShowWindow）。
    /// 视图与 ViewModel 均注册为 Transient（每次打开弹窗都是新实例）。
    /// </para>
    /// </summary>
    /// <param name="name">弹窗名称；缺省为 ViewModel 类型名。可在 ShowDialog / ShowDialogAsync / Show 按名称调用。</param>
    /// <param name="activateExisting">非模态打开时若同类型弹窗已存在，激活现有窗口而不重复创建。</param>
    public static IServiceCollection AddDialog<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        this IServiceCollection services,
        string? name = null,
        bool activateExisting = false)
        where TView : Control
        where TViewModel : class, IDialogAware
    {
        // 注册表与内容弹窗默认宿主（多次 AddDialog 幂等）
        services.TryAddSingleton<DialogRegistry>();
        services.TryAddTransient<DialogWindow>();

        services.AddSingleton(new DialogRegistration(
            name ?? typeof(TViewModel).Name,
            typeof(TViewModel),
            sp => (Control)sp.GetRequiredService<TView>(),
            sp => (IDialogAware)sp.GetRequiredService<TViewModel>(),
            activateExisting));
        services.AddTransient<TViewModel>();
        services.AddTransient<TView>();
        return services;
    }

    /// <summary>
    /// 注册内容弹窗的宿主窗口（仿 Prism 的 RegisterDialogWindow）。
    /// <paramref name="name"/> 为 null 时作为默认宿主；命名宿主通过 <see cref="DialogOptions.HostWindow"/> 选择。
    /// 自定义 chrome 可继承 <see cref="DialogWindow"/> 或直接用任意 Window。
    /// </summary>
    public static IServiceCollection AddDialogWindow<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWindow>(
        this IServiceCollection services,
        string? name = null)
        where TWindow : Window
    {
        services.TryAddSingleton<DialogRegistry>();
        services.AddTransient<TWindow>();
        services.AddSingleton(new DialogHostWindowRegistration(
            name, sp => (Window)sp.GetRequiredService<TWindow>()));
        return services;
    }
}
