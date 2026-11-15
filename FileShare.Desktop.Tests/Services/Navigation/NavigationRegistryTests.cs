using FileShare.Desktop.Services.Navigation;
using FileShare.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace FileShare.Desktop.Tests.Services.Navigation;

/// <summary>
/// 页面注册表：名称查找、重名检测、AddPageForNavigation 的服务注册断言。
/// </summary>
public class NavigationRegistryTests
{
    private sealed class StubViewModel : ViewModelBase
    {
    }

    [Fact]
    public void TryGet_ByName()
    {
        var registry = new NavigationRegistry(new[]
        {
            new PageRegistration("pageA", typeof(StubViewModel),
                sp => new Avalonia.Controls.Control(), sp => new StubViewModel())
        });

        Assert.True(registry.TryGet("pageA", out var registration));
        Assert.Equal(typeof(StubViewModel), registration.ViewModelType);
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public void Constructor_DuplicateName_Throws()
    {
        PageRegistration Make(string name) => new(
            name, typeof(StubViewModel),
            sp => new Avalonia.Controls.Control(), sp => new StubViewModel());

        Assert.Throws<InvalidOperationException>(() => new NavigationRegistry(new[] { Make("same"), Make("same") }));
    }

    [Fact]
    public void AddPageForNavigation_RegistersRegistryRegionManagerAndServices()
    {
        var services = new ServiceCollection();
        services.AddPageForNavigation<Avalonia.Controls.Control, StubViewModel>("pageA");
        services.AddPageForNavigation<Avalonia.Controls.Control, StubViewModel>("pageB");

        using var provider = services.BuildServiceProvider();

        Assert.IsType<StubViewModel>(provider.GetRequiredService<StubViewModel>());
        Assert.IsAssignableFrom<IRegionManager>(provider.GetRequiredService<IRegionManager>());

        var registry = provider.GetRequiredService<NavigationRegistry>();
        Assert.True(registry.TryGet("pageA", out _));
        Assert.True(registry.TryGet("pageB", out _));
    }
}
