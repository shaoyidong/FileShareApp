using Microsoft.Maui.Controls;

namespace FileShare.Mobile.Helpers;

/// <summary>
/// 旋转动画 Behavior：绑定到 VisualElement，根据 IsActive 属性控制持续旋转动画。
/// 对齐 Desktop 项目刷新按钮扫描时的旋转效果（Desktop 使用 Avalonia Animation + Classes.rotate）。
/// 用于 Mobile 项目刷新按钮在 IsScanning=true 时持续旋转。
/// </summary>
public class RotateAnimationBehavior : Behavior<VisualElement>
{
    /// <summary>
    /// 是否激活旋转动画
    /// </summary>
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(
            nameof(IsActive),
            typeof(bool),
            typeof(RotateAnimationBehavior),
            false,
            propertyChanged: OnIsActiveChanged);

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static async void OnIsActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not VisualElement element) return;

        var isActive = (bool)newValue;
        if (isActive)
        {
            // 启动持续旋转
            element.Rotation = 0;
            _ = RotateLoopAsync(element);
        }
        else
        {
            // 停止旋转并复位
            try
            {
                element.CancelAnimations();
            }
            catch { /* 忽略取消异常 */ }
            element.Rotation = 0;
        }
    }

    private static async Task RotateLoopAsync(VisualElement element)
    {
        const uint duration = 1000; // 1秒一圈，对齐 Desktop Animation Duration="0:0:1"
        while (true)
        {
            // 若 Behavior 已分离或 IsActive 变为 false，则退出循环
            var behavior = element.Behaviors.OfType<RotateAnimationBehavior>().FirstOrDefault();
            if (behavior?.IsActive != true) break;

            bool completed = await element.RelRotateToAsync(360, duration, Easing.Linear);
            if (!completed) break;
        }
    }
}
