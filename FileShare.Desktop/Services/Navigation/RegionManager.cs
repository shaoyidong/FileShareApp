using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using FileShare.Desktop.ViewModels;

namespace FileShare.Desktop.Services.Navigation;

/// <summary>区域运行时状态：宿主控件 + 当前页 + 导航日志栈。</summary>
internal sealed class RegionState
{
    /// <summary>导航日志条目：页面名 + 参数。</summary>
    internal sealed record JournalEntry(string Name, NavigationParameters Parameters);

    public RegionState(string name, ContentControl host)
    {
        Name = name;
        Host = host;
    }

    public string Name { get; }

    public ContentControl Host { get; }

    /// <summary>当前显示的页面（未导航过为 null）。</summary>
    public JournalEntry? Current { get; set; }

    private readonly Stack<JournalEntry> _backStack = new();

    public bool CanGoBack => _backStack.Count > 0;

    public JournalEntry? PopBack() => _backStack.TryPop(out var entry) ? entry : null;

    public void PushBack(JournalEntry entry) => _backStack.Push(entry);
}

/// <summary>
/// <see cref="IRegionManager"/> 的实现：管理区域注册、执行导航生命周期、维护导航日志。
/// <para>所有导航操作必须在 UI 线程调用（操作可视树）。</para>
/// </summary>
public sealed class RegionManager : IRegionManager
{
    private readonly NavigationRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, RegionState> _regions = new(StringComparer.Ordinal);

    public RegionManager(NavigationRegistry registry, IServiceProvider serviceProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        // 区域在 XAML 里通过 Region.RegionName 附加属性声明，属性被设置时触发事件；
        // 这里订阅完成注册（应用级单例，随进程存活，无需退订）
        Region.HostRegistered += RegisterRegion;
    }

    /// <inheritdoc />
    public void RegisterRegion(string regionName, ContentControl host)
    {
        if (string.IsNullOrEmpty(regionName))
        {
            throw new ArgumentException("区域名不能为空。", nameof(regionName));
        }

        // 同名区域重复注册（窗口重建等场景）时替换宿主，日志重置
        _regions[regionName] = new RegionState(regionName, host);
    }

    /// <inheritdoc />
    public void RequestNavigate(string regionName, string target, NavigationParameters? parameters = null) =>
        NavigateTo(regionName, target, parameters, replayFromJournal: false);

    /// <inheritdoc />
    public void GoBack(string regionName)
    {
        var region = ResolveRegion(regionName);
        var entry = region.PopBack();
        if (entry is null)
        {
            return;
        }

        NavigateTo(regionName, entry.Name, entry.Parameters, replayFromJournal: true);
    }

    /// <inheritdoc />
    public bool CanGoBack(string regionName) =>
        _regions.TryGetValue(regionName, out var region) && region.CanGoBack;

    /// <inheritdoc />
    public string? GetActivePage(string regionName) =>
        _regions.TryGetValue(regionName, out var region) ? region.Current?.Name : null;

    /// <inheritdoc />
    public void Refresh(string regionName)
    {
        var region = ResolveRegion(regionName);
        if (region.Current is null)
        {
            return;
        }

        // 重建当前页视图、保留同一 VM 实例：ContentControl 整体换内容，{loc:Tr} 重新求值
        var registration = ResolvePage(region.Current.Name);
        var view = registration.ViewFactory(_serviceProvider);
        if (region.Host.Content is Control { DataContext: not null } old)
        {
            view.DataContext = old.DataContext;
        }

        region.Host.Content = view;
    }

    /// <summary>导航核心：生命周期回调整理 + 视图/VM 创建 + 日志维护。UI 线程专用。</summary>
    private void NavigateTo(string regionName, string target, NavigationParameters? parameters, bool replayFromJournal)
    {
        // UI 线程守卫：无 Avalonia 应用时（单元测试 / 设计器）跳过
        if (Application.Current is not null && Dispatcher.UIThread.CheckAccess() is false)
        {
            throw new InvalidOperationException($"{nameof(RequestNavigate)} 必须在 UI 线程调用。");
        }

        var region = ResolveRegion(regionName);
        var registration = ResolvePage(target);
        parameters ??= new NavigationParameters();
        var context = new NavigationContext(target, parameters);

        // 同名目标且当前 VM 声明可复用：不重建，只更新参数
        if (region.Host.Content is Control { DataContext: ViewModelBase current } &&
            current.GetType() == registration.ViewModelType &&
            current is INavigationAware reusable &&
            reusable.IsNavigationTarget(context))
        {
            reusable.OnNavigatedTo(context);
            region.Current = new RegionState.JournalEntry(target, parameters);
            return;
        }

        // 出站通知（旧页实现 INavigationAware 才有）
        if (region.Host.Content is Control { DataContext: INavigationAware leaving })
        {
            leaving.OnNavigatedFrom(context);
        }

        // 创建新页（显式 lambda 工厂，AOT 安全）
        var view = registration.ViewFactory(_serviceProvider);
        var viewModel = registration.ViewModelFactory(_serviceProvider);
        view.DataContext = viewModel;

        // 日志：正常导航压栈，GoBack 重放不压
        if (!replayFromJournal && region.Current is { } previous)
        {
            region.PushBack(previous);
        }

        region.Current = new RegionState.JournalEntry(target, parameters);
        region.Host.Content = view;

        if (viewModel is INavigationAware entering)
        {
            entering.OnNavigatedTo(context);
        }
    }

    private RegionState ResolveRegion(string regionName)
    {
        if (_regions.TryGetValue(regionName, out var region))
        {
            return region;
        }

        throw new InvalidOperationException($"No region registered with name '{regionName}'.");
    }

    private PageRegistration ResolvePage(string name)
    {
        if (_registry.TryGet(name, out var registration))
        {
            return registration;
        }

        throw new InvalidOperationException($"No navigation page registered with name '{name}'.");
    }
}
