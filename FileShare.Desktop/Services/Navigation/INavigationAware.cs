namespace FileShare.Desktop.Services.Navigation;

/// <summary>
/// 页面 ViewModel 的导航生命周期契约（仿 Prism 的 INavigationAware）。
/// <para>实现本接口的页面 VM 在区域导航时会收到进入 / 离开通知，并可控制实例复用。</para>
/// </summary>
public interface INavigationAware
{
    /// <summary>当前页是否复用既有实例：目标页与当前页同名时，返回 true 只触发 OnNavigatedTo（更新参数），false 则重建实例。</summary>
    bool IsNavigationTarget(NavigationContext context);

    /// <summary>导航进入本页时调用（在视图显示之前）。</summary>
    void OnNavigatedTo(NavigationContext context);

    /// <summary>导航离开本页时调用。</summary>
    void OnNavigatedFrom(NavigationContext context);
}
