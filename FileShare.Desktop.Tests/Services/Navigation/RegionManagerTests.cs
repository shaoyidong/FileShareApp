using Avalonia.Controls;
using FileShare.Desktop.Services.Navigation;
using FileShare.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace FileShare.Desktop.Tests.Services.Navigation;

/// <summary>
/// 区域导航核心行为：生命周期顺序、导航日志回退、实例复用、Refresh 保留 VM。
/// 视图用纯 Control（不实例化 Window，测试进程没有 Avalonia 窗口平台）。
/// </summary>
public class RegionManagerTests
{
    /// <summary>记录生命周期事件的桩页面 VM。</summary>
    private sealed class TestPageViewModel : ViewModelBase, INavigationAware
    {
        public List<string> Events { get; } = new();
        public bool ReuseTarget { get; set; }

        public bool IsNavigationTarget(NavigationContext context) => ReuseTarget;

        public void OnNavigatedTo(NavigationContext context) => Events.Add($"To:{context.Name}");

        public void OnNavigatedFrom(NavigationContext context) => Events.Add($"From:{context.Name}");
    }

    private static (IRegionManager Manager, ContentControl Host) CreateManager(params string[] pages)
    {
        var services = new ServiceCollection();
        foreach (var page in pages)
        {
            services.AddPageForNavigation<Control, TestPageViewModel>(page);
        }

        var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IRegionManager>();
        var host = new ContentControl();
        manager.RegisterRegion("TestRegion", host);
        return (manager, host);
    }

    private static TestPageViewModel CurrentVm(ContentControl host) =>
        (TestPageViewModel)((Control)host.Content!).DataContext!;

    [Fact]
    public void GetActivePage_BeforeNavigation_IsNull()
    {
        var (manager, _) = CreateManager("pageA");

        Assert.Null(manager.GetActivePage("TestRegion"));
        Assert.Null(manager.GetActivePage("UnknownRegion"));
    }

    [Fact]
    public void Navigate_CreatesViewWithVmAndRaisesOnNavigatedTo()
    {
        var (manager, host) = CreateManager("pageA");

        manager.RequestNavigate("TestRegion", "pageA");

        Assert.NotNull(host.Content);
        var vm = CurrentVm(host);
        Assert.Contains("To:pageA", vm.Events);
        Assert.Equal("pageA", manager.GetActivePage("TestRegion"));
    }

    [Fact]
    public void Navigate_SecondPage_RaisesFromOnLeavingAndToOnEntering()
    {
        var (manager, host) = CreateManager("pageA", "pageB");
        manager.RequestNavigate("TestRegion", "pageA");
        var first = CurrentVm(host);

        manager.RequestNavigate("TestRegion", "pageB");

        Assert.Contains("From:pageB", first.Events);
        Assert.Equal("pageB", manager.GetActivePage("TestRegion"));
        Assert.NotSame(first, CurrentVm(host));
        Assert.Contains("To:pageB", CurrentVm(host).Events);
    }

    [Fact]
    public void GoBack_ReturnsToPreviousEntry()
    {
        var (manager, host) = CreateManager("pageA", "pageB");
        manager.RequestNavigate("TestRegion", "pageA");
        manager.RequestNavigate("TestRegion", "pageB");

        Assert.True(manager.CanGoBack("TestRegion"));
        manager.GoBack("TestRegion");

        Assert.Equal("pageA", manager.GetActivePage("TestRegion"));
        Assert.False(manager.CanGoBack("TestRegion"));
        Assert.Contains("To:pageA", CurrentVm(host).Events);
    }

    [Fact]
    public void GoBack_EmptyStack_IsNoOp()
    {
        var (manager, host) = CreateManager("pageA");
        manager.RequestNavigate("TestRegion", "pageA");

        manager.GoBack("TestRegion");

        Assert.Equal("pageA", manager.GetActivePage("TestRegion"));
        Assert.Contains("To:pageA", CurrentVm(host).Events);
    }

    [Fact]
    public void Navigate_SamePageWithReuseTarget_ReusesInstance()
    {
        var (manager, host) = CreateManager("pageA");
        manager.RequestNavigate("TestRegion", "pageA");
        var first = CurrentVm(host);
        first.ReuseTarget = true;

        manager.RequestNavigate("TestRegion", "pageA");

        Assert.Same(first, CurrentVm(host));
        Assert.Equal(2, first.Events.Count(e => e == "To:pageA"));
    }

    [Fact]
    public void Navigate_SamePageWithoutReuseTarget_RecreatesInstance()
    {
        var (manager, host) = CreateManager("pageA");
        manager.RequestNavigate("TestRegion", "pageA");
        var first = CurrentVm(host);

        manager.RequestNavigate("TestRegion", "pageA");

        Assert.NotSame(first, CurrentVm(host));
    }

    [Fact]
    public void Refresh_RebuildsViewButKeepsSameViewModel()
    {
        var (manager, host) = CreateManager("pageA");
        manager.RequestNavigate("TestRegion", "pageA");
        var vm = CurrentVm(host);
        var oldView = (Control)host.Content!;

        manager.Refresh("TestRegion");

        Assert.NotSame(oldView, host.Content);
        Assert.Same(vm, CurrentVm(host));
    }

    [Fact]
    public void Navigate_UnknownPage_Throws()
    {
        var (manager, _) = CreateManager("pageA");

        Assert.Throws<InvalidOperationException>(() => manager.RequestNavigate("TestRegion", "missing"));
    }

    [Fact]
    public void Navigate_UnknownRegion_Throws()
    {
        var (manager, _) = CreateManager("pageA");

        Assert.Throws<InvalidOperationException>(() => manager.RequestNavigate("MissingRegion", "pageA"));
    }
}
