using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FileShare.Desktop.ViewModels;
using System;

namespace FileShare.Desktop.Views;

/// <summary>
/// 关于窗口：Qoder 风格的无边框浮动卡片。
/// 鼠标悬停在卡片上时卡片朝鼠标所在角落倾斜，像被「踩住」一角一样；
/// 指针离开后阻尼动画回到水平；点击主窗口（失活）时自动收起。
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>最大倾斜角（度）。</summary>
    private const double MaxTiltDegrees = 1.5;

    /// <summary>倾斜复位动画的步进间隔。</summary>
    private static readonly TimeSpan TiltResetInterval = TimeSpan.FromMilliseconds(16);

    private readonly Rotate3DTransform _tiltTransform = new(0, 0, 0, 0, 0, 0, 600);
    private DispatcherTimer? _tiltResetTimer;
    private bool _activatedOnce;
    private bool _closing;

    public AboutWindow()
    {
        InitializeComponent();
        if (Design.IsDesignMode)
        {
            // 预览器里没有 DialogService，直接建 VM 便于设计时绑定
            DataContext = new AboutViewModel();
        }

        TiltRoot.RenderTransform = _tiltTransform;

        Activated += (_, _) => _activatedOnce = true;
        Closed += (_, _) =>
        {
            _tiltResetTimer?.Stop();
            // VM 持有静态事件订阅，窗口关闭时释放，避免窗口被静态事件持有
            (DataContext as IDisposable)?.Dispose();
        };

        // 浮动卡片语义：主窗口抢回焦点即收起。
        // 必须用 Post 延迟关闭：在 WM_ACTIVATE(WA_INACTIVE) 处理栈内同步 Close() owned 窗口
        // 会触发 Avalonia Win32 的激活修复失败（Avalonia#4868），把 owner 主窗口错误最小化。
        Deactivated += (_, _) =>
        {
            if (_activatedOnce)
            {
                Dispatcher.UIThread.Post(CloseIfNotClosing);
            }
        };
        PointerExited += (_, _) => StartTiltReset();
    }

    private void CloseIfNotClosing()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    /// <summary>
    /// 鼠标位置换算成 3D 倾斜角：鼠标在哪一角，哪一角就像被踩住一样向后沉。
    /// 中心为旋转原点，横向偏移转成绕 Y 轴角度，纵向偏移转成绕 X 轴角度。
    /// </summary>
    private void OnTiltSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        var bounds = TiltRoot.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var point = e.GetPosition(TiltRoot);
        var nx = Math.Clamp(point.X / bounds.Width * 2 - 1, -1, 1);  // -1 左 … +1 右
        var ny = Math.Clamp(point.Y / bounds.Height * 2 - 1, -1, 1); // -1 上 … +1 下

        _tiltResetTimer?.Stop();

        //_tiltTransform.CenterX = bounds.Width / 2;
        //_tiltTransform.CenterY = bounds.Height / 2;
        _tiltTransform.AngleY = nx * MaxTiltDegrees;  // 鼠标在右 → 右缘向后倾
        _tiltTransform.AngleX = -ny * MaxTiltDegrees; // 鼠标在下 → 底缘向后倾
    }

    /// <summary>指针离开后用简单的阻尼步进把倾斜角归零。</summary>
    private void StartTiltReset()
    {
        if (_tiltResetTimer is null)
        {
            _tiltResetTimer = new DispatcherTimer { Interval = TiltResetInterval };
            _tiltResetTimer.Tick += (_, _) =>
            {
                var x = _tiltTransform.AngleX;
                var y = _tiltTransform.AngleY;
                if (Math.Abs(x) < 0.4 && Math.Abs(y) < 0.4)
                {
                    _tiltTransform.AngleX = 0;
                    _tiltTransform.AngleY = 0;
                    _tiltResetTimer.Stop();
                    return;
                }

                _tiltTransform.AngleX = x * 0.8;
                _tiltTransform.AngleY = y * 0.8;
            };
        }

        _tiltResetTimer.Start();
    }
}
