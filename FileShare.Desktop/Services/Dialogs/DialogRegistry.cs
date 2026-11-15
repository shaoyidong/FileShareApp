using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 一条弹窗注册：ViewModel 类型 + 视图工厂。
/// <para>
/// 视图工厂返回 <see cref="Window"/>（自带 chrome 的窗口弹窗，行为同 ShowDialogWindow / ShowWindow）
/// 或 UserControl（内容弹窗，仿 Prism ShowDialog：由服务放入宿主窗口显示）。
/// 工厂是注册时闭包住的显式 lambda，NativeAOT 可静态分析，不做任何反射构造。
/// </para>
/// </summary>
public sealed class DialogRegistration
{
    /// <summary>弹窗名称（未显式指定时默认为 ViewModel 类型名）。</summary>
    public string Name { get; }

    /// <summary>弹窗 ViewModel 类型。</summary>
    public Type ViewModelType { get; }

    /// <summary>创建弹窗视图（Window 或 UserControl）。</summary>
    public Func<IServiceProvider, Control> ViewFactory { get; }

    /// <summary>创建弹窗 ViewModel。</summary>
    public Func<IServiceProvider, IDialogAware> ViewModelFactory { get; }

    /// <summary>非模态打开时若同类型弹窗已存在，激活现有窗口而不重复创建。</summary>
    public bool ActivateExisting { get; }

    public DialogRegistration(
        string name,
        Type viewModelType,
        Func<IServiceProvider, Control> viewFactory,
        Func<IServiceProvider, IDialogAware> viewModelFactory,
        bool activateExisting = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ViewModelType = viewModelType ?? throw new ArgumentNullException(nameof(viewModelType));
        ViewFactory = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));
        ViewModelFactory = viewModelFactory ?? throw new ArgumentNullException(nameof(viewModelFactory));
        ActivateExisting = activateExisting;
    }
}

/// <summary>内容弹窗宿主窗口的一条注册（仿 Prism 的 RegisterDialogWindow）。</summary>
/// <param name="Name">宿主名称；null 表示默认宿主（内容弹窗未指定 HostWindow 时使用）。</param>
/// <param name="Factory">创建宿主窗口。</param>
public sealed class DialogHostWindowRegistration(string? name, Func<IServiceProvider, Window> factory)
{
    /// <summary>宿主名称；null 表示默认宿主。</summary>
    public string? Name { get; } = name;

    /// <summary>创建宿主窗口。</summary>
    public Func<IServiceProvider, Window> Factory { get; } = factory;
}

/// <summary>
/// 弹窗注册表：收集 <see cref="DialogRegistration"/> 与宿主窗口注册，按名称或 ViewModel 类型查找。
/// <para>由 <c>services.AddDialog&lt;TView, TViewModel&gt;()</c> / <c>AddDialogWindow&lt;TWindow&gt;()</c>
/// 显式注册，无程序集扫描。</para>
/// </summary>
public sealed class DialogRegistry
{
    private readonly Dictionary<string, DialogRegistration> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, DialogRegistration> _byViewModelType = new();
    private readonly Dictionary<string, Func<IServiceProvider, Window>> _hostWindows = new(StringComparer.Ordinal);
    private Func<IServiceProvider, Window>? _defaultHostFactory;

    /// <summary>MEDI 会把所有 AddDialog / AddDialogWindow 注册的单例注入进来。</summary>
    public DialogRegistry(
        IEnumerable<DialogRegistration> registrations,
        IEnumerable<DialogHostWindowRegistration> hostWindows)
    {
        if (registrations is not null)
        {
            foreach (var registration in registrations)
            {
                if (!_byName.TryAdd(registration.Name, registration))
                {
                    throw new InvalidOperationException($"Duplicate dialog name '{registration.Name}'.");
                }

                if (!_byViewModelType.TryAdd(registration.ViewModelType, registration))
                {
                    throw new InvalidOperationException(
                        $"Duplicate dialog registration for view model '{registration.ViewModelType.Name}'.");
                }
            }
        }

        if (hostWindows is not null)
        {
            foreach (var host in hostWindows)
            {
                if (string.IsNullOrEmpty(host.Name))
                {
                    _defaultHostFactory = host.Factory;
                }
                else if (!_hostWindows.TryAdd(host.Name, host.Factory))
                {
                    throw new InvalidOperationException($"Duplicate dialog host window name '{host.Name}'.");
                }
            }
        }
    }

    /// <summary>按名称查找弹窗注册。</summary>
    public bool TryGet(string name, out DialogRegistration registration) =>
        _byName.TryGetValue(name, out registration!);

    /// <summary>按 ViewModel 类型查找弹窗注册。</summary>
    public bool TryGet(Type viewModelType, out DialogRegistration registration) =>
        _byViewModelType.TryGetValue(viewModelType, out registration!);

    /// <summary>
    /// 查找内容弹窗的宿主窗口工厂：先按名称找注册的自定义宿主，找不到退回默认宿主。
    /// </summary>
    public bool TryGetHostWindow(string? name, out Func<IServiceProvider, Window> factory)
    {
        if (!string.IsNullOrEmpty(name) && _hostWindows.TryGetValue(name, out factory!))
        {
            return true;
        }

        if (_defaultHostFactory is not null)
        {
            factory = _defaultHostFactory;
            return true;
        }

        factory = null!;
        return false;
    }
}
