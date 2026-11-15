using Avalonia.Controls;

namespace FileShare.Desktop.Services.Navigation;

/// <summary>
/// 仿 Prism 区域管理器的区域导航门面：区域注册、按名称导航、导航日志回退。
/// <para>区域在 XAML 里用 <see cref="Region.RegionName"/> 附加属性声明；
/// 页面在 Program 里用 <c>services.AddPageForNavigation&lt;TView, TViewModel&gt;()</c> 注册。</para>
/// </summary>
public interface IRegionManager
{
    /// <summary>注册一个区域宿主（由 <see cref="Region.RegionName"/> 附加属性触发，通常无需手工调用）。</summary>
    void RegisterRegion(string regionName, ContentControl host);

    /// <summary>导航到指定页面（未注册的页面名或区域名抛异常）。</summary>
    void RequestNavigate(string regionName, string target, NavigationParameters? parameters = null);

    /// <summary>沿导航日志回退一步（空栈时 no-op）。</summary>
    void GoBack(string regionName);

    /// <summary>区域是否有可回退的历史。</summary>
    bool CanGoBack(string regionName);

    /// <summary>区域当前显示的页面名称（未导航过为 null）。</summary>
    string? GetActivePage(string regionName);

    /// <summary>用当前页的注册工厂重建视图（保留同一 ViewModel 实例）——用于语言切换后让 {loc:Tr} 重新求值。</summary>
    void Refresh(string regionName);
}
