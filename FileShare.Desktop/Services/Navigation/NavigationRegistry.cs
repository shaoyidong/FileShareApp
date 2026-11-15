using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using FileShare.Desktop.ViewModels;

namespace FileShare.Desktop.Services.Navigation;

/// <summary>
/// 一条页面注册：页面名称 + ViewModel 类型 + 视图 / VM 工厂。
/// <para>工厂是注册时闭包住的显式 lambda，NativeAOT 可静态分析，不做任何反射构造。</para>
/// </summary>
public sealed class PageRegistration
{
    /// <summary>页面名称。</summary>
    public string Name { get; }

    /// <summary>页面 ViewModel 类型。</summary>
    public Type ViewModelType { get; }

    /// <summary>创建页面视图。</summary>
    public Func<IServiceProvider, Control> ViewFactory { get; }

    /// <summary>创建页面 ViewModel。</summary>
    public Func<IServiceProvider, ViewModelBase> ViewModelFactory { get; }

    public PageRegistration(
        string name,
        Type viewModelType,
        Func<IServiceProvider, Control> viewFactory,
        Func<IServiceProvider, ViewModelBase> viewModelFactory)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ViewModelType = viewModelType ?? throw new ArgumentNullException(nameof(viewModelType));
        ViewFactory = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));
        ViewModelFactory = viewModelFactory ?? throw new ArgumentNullException(nameof(viewModelFactory));
    }
}

/// <summary>
/// 页面注册表：收集 <see cref="PageRegistration"/>，按名称查找。
/// <para>由 <c>services.AddPageForNavigation&lt;TView, TViewModel&gt;()</c> 显式注册，无程序集扫描。</para>
/// </summary>
public sealed class NavigationRegistry
{
    private readonly Dictionary<string, PageRegistration> _byName = new(StringComparer.Ordinal);

    /// <summary>MEDI 会把所有 AddPageForNavigation 注册的 <see cref="PageRegistration"/> 单例注入进来。</summary>
    public NavigationRegistry(IEnumerable<PageRegistration> registrations)
    {
        if (registrations is null)
        {
            return;
        }

        foreach (var registration in registrations)
        {
            if (!_byName.TryAdd(registration.Name, registration))
            {
                throw new InvalidOperationException($"Duplicate navigation page name '{registration.Name}'.");
            }
        }
    }

    /// <summary>按名称查找页面注册。</summary>
    public bool TryGet(string name, out PageRegistration registration) =>
        _byName.TryGetValue(name, out registration!);
}
