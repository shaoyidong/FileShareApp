using Avalonia;
using Avalonia.Controls;
using System;

namespace FileShare.Desktop.Services.Navigation;

/// <summary>
/// 区域声明附加属性（仿 Prism 的 Region.RegionName）：
/// XAML 里 <c>&lt;ContentControl nav:Region.RegionName="MainRegion" /&gt;</c> 即声明一个区域宿主。
/// <para>
/// 属性被设置时触发 <see cref="HostRegistered"/> 静态事件；
/// <see cref="RegionManager"/>（DI 单例）在构造时订阅该事件完成注册——
/// 用事件桥接避免附加属性处理器依赖静态服务定位器（App.ServiceProvider 之类）。
/// </para>
/// </summary>
public static class Region
{
    /// <summary>区域宿主注册事件：参数为（区域名, 宿主控件）。设计器等无 RegionManager 场景无订阅者，静默跳过。</summary>
    public static event Action<string, ContentControl>? HostRegistered;

    /// <summary>区域名称附加属性。</summary>
    public static readonly AttachedProperty<string?> RegionNameProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("RegionName", typeof(Region));

    static Region()
    {
        RegionNameProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is string name &&
                !string.IsNullOrEmpty(name) &&
                control is ContentControl host)
            {
                HostRegistered?.Invoke(name, host);
            }
        });
    }

    /// <summary>设置区域名称（XAML 附加属性语法的配套访问器）。</summary>
    public static void SetRegionName(Control control, string? value) =>
        control.SetValue(RegionNameProperty, value);

    /// <summary>读取区域名称。</summary>
    public static string? GetRegionName(Control control) =>
        control.GetValue(RegionNameProperty);
}
