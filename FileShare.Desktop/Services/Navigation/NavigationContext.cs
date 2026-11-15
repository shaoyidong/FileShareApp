using System;

namespace FileShare.Desktop.Services.Navigation;

/// <summary>
/// 导航参数键值集合（仿 Prism 的 NavigationParameters），实现逻辑在 <see cref="ParametersBase"/>。
/// </summary>
public sealed class NavigationParameters : ParametersBase
{
}

/// <summary>
/// 一次导航的上下文：目标页名称 + 参数。
/// </summary>
public sealed class NavigationContext
{
    /// <summary>目标页名称（AddPageForNavigation 注册的名称）。</summary>
    public string Name { get; }

    /// <summary>导航参数。</summary>
    public NavigationParameters Parameters { get; }

    public NavigationContext(string name, NavigationParameters? parameters)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Parameters = parameters ?? new NavigationParameters();
    }
}
