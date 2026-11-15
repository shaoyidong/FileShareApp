using FileShare.Desktop.Services.Dialogs;
using FileShare.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using Xunit;

namespace FileShare.Desktop.Tests.Services.Dialogs;

/// <summary>
/// 弹窗注册表：名称 / ViewModel 类型查找，内容弹窗宿主窗口的默认与命名选择。
/// </summary>
public class DialogRegistryTests
{
    private static DialogRegistration MakeRegistration(string name) => new(
        name,
        typeof(SendTextViewModel),
        sp => new Avalonia.Controls.UserControl(),
        sp => new SendTextViewModel());

    private static DialogHostWindowRegistration MakeHost(string? name) => new(
        name, sp => throw new InvalidOperationException("宿主工厂不应在查找阶段被调用"));

    [Fact]
    public void TryGet_ByNameAndViewModelType()
    {
        var registry = new DialogRegistry(new[] { MakeRegistration("SendTextDialog") }, Array.Empty<DialogHostWindowRegistration>());

        Assert.True(registry.TryGet("SendTextDialog", out var byName));
        Assert.Equal(typeof(SendTextViewModel), byName.ViewModelType);

        Assert.True(registry.TryGet(typeof(SendTextViewModel), out var byType));
        Assert.Equal("SendTextDialog", byType.Name);

        Assert.False(registry.TryGet("missing", out _));
        Assert.False(registry.TryGet(typeof(MessageReceivedViewModel), out _));
    }

    [Fact]
    public void Constructor_DuplicateName_Throws()
    {
        var registrations = new[] { MakeRegistration("same"), MakeRegistration("same") };

        Assert.Throws<InvalidOperationException>(() => new DialogRegistry(registrations, Array.Empty<DialogHostWindowRegistration>()));
    }

    [Fact]
    public void TryGetHostWindow_NullName_UsesDefaultHost()
    {
        var registry = new DialogRegistry(
            Array.Empty<DialogRegistration>(),
            new[] { MakeHost(null), MakeHost("fancy") });

        Assert.True(registry.TryGetHostWindow(null, out var factory));
        Assert.NotNull(factory);
    }

    [Fact]
    public void TryGetHostWindow_NamedHost_TakesPriorityOverDefault()
    {
        var registry = new DialogRegistry(
            Array.Empty<DialogRegistration>(),
            new[] { MakeHost(null), MakeHost("fancy") });

        Assert.True(registry.TryGetHostWindow("fancy", out _));
        // 未注册的名称退回默认宿主，而不是失败
        Assert.True(registry.TryGetHostWindow("missing", out _));
    }

    [Fact]
    public void TryGetHostWindow_NoHosts_ReturnsFalse()
    {
        var registry = new DialogRegistry(Array.Empty<DialogRegistration>(), Array.Empty<DialogHostWindowRegistration>());

        Assert.False(registry.TryGetHostWindow(null, out _));
        Assert.False(registry.TryGetHostWindow("any", out _));
    }

    [Fact]
    public void AddDialog_RegistersViewViewModelAndRegistry()
    {
        var services = new ServiceCollection();
        services.AddDialog<Avalonia.Controls.UserControl, SendTextViewModel>("SendTextDialog");

        // 默认宿主 DialogWindow 已随内容弹窗注册（不实例化：测试进程没有 Avalonia 窗口平台）
        Assert.Contains(services, s => s.ServiceType == typeof(DialogWindow));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<SendTextViewModel>(provider.GetRequiredService<SendTextViewModel>());

        var registry = provider.GetRequiredService<DialogRegistry>();
        Assert.True(registry.TryGet("SendTextDialog", out var registration));
        Assert.Equal(typeof(SendTextViewModel), registration.ViewModelType);
    }

    [Fact]
    public void AddDialogWindow_RegistersNamedAndDefaultHost()
    {
        var services = new ServiceCollection();
        services.AddDialogWindow<DialogWindow>("fancy");
        services.AddDialogWindow<DialogWindow>();

        var registry = services.BuildServiceProvider().GetRequiredService<DialogRegistry>();

        Assert.True(registry.TryGetHostWindow("fancy", out _));
        Assert.True(registry.TryGetHostWindow(null, out var defaultFactory));
        Assert.NotNull(defaultFactory);
    }
}
