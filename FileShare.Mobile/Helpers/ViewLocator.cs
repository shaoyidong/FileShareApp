using Microsoft.Extensions.DependencyInjection;
using FileShare.Mobile.ViewModels;
using FileShare.Mobile.Views;

namespace FileShare.Mobile.Helpers;

/// <summary>
/// ViewLocator：将 ViewModel 解析到对应的 View，并设置 BindingContext。
/// 对齐 Desktop 项目的 ViewLocator，提供集中的 VM-to-View 映射。
/// </summary>
/// <remarks>
/// Desktop 项目中 ViewLocator 实现 Avalonia 的 IDataTemplate，
/// 在 ContentControl 的 Content 为 VM 时自动解析 View。
/// MAUI Shell 通过 ContentTemplate + DI 构造函数注入实现 V/VM 绑定，
/// 本类作为集中的 VM-View 映射注册表，供需要按 VM 显式创建 Page 时使用，
/// 并在应用启动时用于注册 Shell 路由。
/// </remarks>
public class ViewLocator
{
    private readonly IServiceProvider _serviceProvider;

    public ViewLocator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 根据 VM 实例解析对应的 View，并设置 BindingContext
    /// </summary>
    public ContentPage Build(ViewModelBase viewModel)
    {
        ContentPage page = viewModel switch
        {
            MainPageViewModel => _serviceProvider.GetRequiredService<MainPage>(),
            HistoryViewModel => _serviceProvider.GetRequiredService<HistoryPage>(),
            AppListViewModel => _serviceProvider.GetRequiredService<AppListPage>(),
            _ => throw new InvalidOperationException($"No view for {viewModel.GetType().Name}")
        };
        // 关键：将 ViewModel 设置到视图的 BindingContext
        page.BindingContext = viewModel;
        return page;
    }

    /// <summary>
    /// 创建并解析指定 ViewModel 类型的 View
    /// </summary>
    public TPage Build<TPage, TViewModel>()
        where TPage : ContentPage
        where TViewModel : ViewModelBase
    {
        var viewModel = _serviceProvider.GetRequiredService<TViewModel>();
        var page = _serviceProvider.GetRequiredService<TPage>();
        page.BindingContext = viewModel;
        return page;
    }

    /// <summary>
    /// 根据 VM 类型查找 View 类型
    /// </summary>
    public static Type GetViewTypeFor(Type viewModelType)
    {
        return viewModelType.Name switch
        {
            nameof(MainPageViewModel) => typeof(MainPage),
            nameof(HistoryViewModel) => typeof(HistoryPage),
            nameof(AppListViewModel) => typeof(AppListPage),
            _ => throw new InvalidOperationException($"No view registered for {viewModelType.Name}")
        };
    }

    /// <summary>
    /// 根据 VM 类型查找 View 类型（泛型版本）
    /// </summary>
    public static Type GetViewTypeFor<TViewModel>() where TViewModel : ViewModelBase
        => GetViewTypeFor(typeof(TViewModel));

    /// <summary>
    /// 注册 Shell 路由：基于 VM-View 映射，按 View 名注册路由
    /// </summary>
    public static void RegisterRoutes()
    {
        // 当前 MainPage、HistoryPage 通过 ShellContent 注册，无需再次注册路由
        // AppListPage 已通过 AddSingletonWithShellRoute 注册
        // 此方法作为集中入口，便于后续扩展
    }
}
