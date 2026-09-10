using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace FileShare.Desktop.Behaviors
{
    public static class RotationBehavior
    {
        // 定义附加属性 IsSpinning
        public static readonly AttachedProperty<bool> IsSpinningProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("IsSpinning", typeof(RotationBehavior));

        // 存储每个控件的 CancellationTokenSource，用于停止动画
        private static readonly AttachedProperty<CancellationTokenSource?> CancellationProperty =
            AvaloniaProperty.RegisterAttached<Control, CancellationTokenSource?>("Cancellation", typeof(RotationBehavior));

        static RotationBehavior()
        {
            IsSpinningProperty.Changed.AddClassHandler<Control>((control, e) =>
            {
                var isSpinning = e.NewValue is true;
                if (isSpinning)
                    StartSpin(control);
                else
                    StopSpin(control);
            });
        }

        public static void SetIsSpinning(Control element, bool value) =>
            element.SetValue(IsSpinningProperty, value);

        public static bool GetIsSpinning(Control element) =>
            element.GetValue(IsSpinningProperty);

        private static void StartSpin(Control control)
        {
            // 停止已有动画
            StopSpin(control);

            // 确保有 RenderTransform
            if (control.RenderTransform is not RotateTransform rotateTransform)
            {
                rotateTransform = new RotateTransform(0);
                control.RenderTransform = rotateTransform;
                // 设置旋转中心为控件中心（默认已是 50%,50%）
            }

            // 创建无限旋转动画（2秒一圈）
            var animation = new Animation
            {
                Duration = TimeSpan.FromSeconds(2),
                IterationCount = IterationCount.Infinite,
                Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 0d) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(RotateTransform.AngleProperty, 360d) }
                }
            }
            };

            // 使用 CancellationTokenSource 控制动画停止
            var cts = new CancellationTokenSource();
            control.SetValue(CancellationProperty, cts);

            // 启动动画（异步，不阻塞 UI）
            _ = animation.RunAsync(control, cts.Token);
        }

        private static void StopSpin(Control control)
        {
            var cts = control.GetValue(CancellationProperty);
            cts?.Cancel();
            control.SetValue(CancellationProperty, null);
        }
    }
}
