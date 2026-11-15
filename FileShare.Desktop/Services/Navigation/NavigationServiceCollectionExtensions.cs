using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Diagnostics.CodeAnalysis;
using FileShare.Desktop.ViewModels;

namespace FileShare.Desktop.Services.Navigation;

/// <summary>
/// 仿 Prism 的 <c>IContainerRegistry.RegisterForNavigation&lt;TView, TViewModel&gt;()</c>：
/// 用显式泛型注册导航页面，不用字符串查类型 / 程序集扫描，NativeAOT 安全。
/// </summary>
public static class NavigationServiceCollectionExtensions
{
    /// <summary>
    /// 注册一个导航页面：视图 <typeparamref name="TView"/> 与 ViewModel <typeparamref name="TViewModel"/>。
    /// </summary>
    /// <param name="name">页面名称（RequestNavigate / GoBack 按此名称导航）。</param>
    /// <param name="viewModelFactory">
    /// 自定义 VM 工厂；为 null 时 ViewModel 注册为 Transient 由 DI 解析。
    /// 传入工厂时不重复注册 VM（适用于单例 VM，如主界面）。
    /// </param>
    public static IServiceCollection AddPageForNavigation<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, TViewModel>? viewModelFactory = null)
        where TView : Control
        where TViewModel : ViewModelBase
    {
        services.TryAddSingleton<NavigationRegistry>();
        services.TryAddSingleton<IRegionManager>(sp => new RegionManager(
            sp.GetRequiredService<NavigationRegistry>(), sp));

        services.AddSingleton(new PageRegistration(
            name ?? throw new ArgumentNullException(nameof(name)),
            typeof(TViewModel),
            sp => (Control)sp.GetRequiredService<TView>(),
            sp => (ViewModelBase)(viewModelFactory is not null
                ? viewModelFactory(sp)
                : sp.GetRequiredService<TViewModel>())));
        services.AddTransient<TView>();
        if (viewModelFactory is null)
        {
            services.AddTransient<TViewModel>();
        }

        return services;
    }
}
