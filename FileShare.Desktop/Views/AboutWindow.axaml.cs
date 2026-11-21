using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FileShare.Desktop.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;

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

    /// <summary>Logo 自转速度（度/秒），约 12 秒一圈。</summary>
    private const double LogoRotationDegreesPerSecond = 30;

    /// <summary>悬停 / 移开时转速过渡的时间常数（秒）：越大越绵长，越小越跟手。</summary>
    private const double LogoRotationEaseSeconds = 0.35;

    /// <summary>旋转推进间隔，与倾斜复位动画一致。</summary>
    private static readonly TimeSpan LogoRotationInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>倾斜复位动画的步进间隔。</summary>
    private static readonly TimeSpan TiltResetInterval = TimeSpan.FromMilliseconds(16);

    private readonly ILogger<AboutWindow> _logger;
    private readonly SynchronizationContext _uiContext;
    private readonly Rotate3DTransform _tiltTransform = new(0, 0, 0, 0, 0, 0, 600);
    private readonly RotateTransform _logoRotation = new();
    private DispatcherTimer? _tiltResetTimer;
    private readonly DispatcherTimer? _logoRotationTimer;
    private long _logoRotationLastTicks;
    private double _logoRotationSpeed = 1;
    private double _logoRotationAngle;
    private bool _logoHovered;
    private bool _activatedOnce;
    private bool _closing;
    private LayeredWindowRenderer? _layeredRenderer;

    public AboutWindow() : this(
        NullLogger<AboutWindow>.Instance,
        SynchronizationContext.Current ?? new SynchronizationContext())
    {
    }

    public AboutWindow(ILogger<AboutWindow> logger, SynchronizationContext uiContext)
    {
        _logger = logger;
        _uiContext = uiContext;
        InitializeComponent();
        if (Design.IsDesignMode)
        {
            // 预览器里没有 DialogService，直接建 VM 便于设计时绑定
            DataContext = new AboutViewModel();
        }

        TiltRoot.RenderTransform = _tiltTransform;

        // Logo 自转：RenderTransform 不参与布局，72×72 的尺寸 / 位置与原有交互都不变；
        // 仅在 Logo 自身挂悬停事件，卡片的 3D 倾斜与窗口失活收起逻辑完全不受影响。
        LogoImage.RenderTransform = _logoRotation;
        LogoImage.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        LogoImage.PointerEntered += (_, _) => _logoHovered = true;
        LogoImage.PointerExited += (_, _) => _logoHovered = false;
        _logoRotationLastTicks = Environment.TickCount64;
        _logoRotationTimer = new DispatcherTimer { Interval = LogoRotationInterval };
        _logoRotationTimer.Tick += OnLogoTick;
        _logoRotationTimer.Start();

        Activated += (_, _) => _activatedOnce = true;
        Opened += (_, _) =>
        {
            // Win7 拿不到 Avalonia 透明窗口，改用分层窗口自行合成；其余平台继续走原生透明
            if (!TryEnableLayeredMode())
            {
                ApplyOpaqueCardFallbackIfNeeded();
            }
        };
        // 动态内容（Headline/二维码/链接）完成布局后需要重新抓一帧
        LayoutUpdated += (_, _) => _layeredRenderer?.RequestUpdate();
        Closed += (_, _) =>
        {
            _tiltResetTimer?.Stop();
            _logoRotationTimer?.Stop();
            _layeredRenderer?.Dispose();
            _layeredRenderer = null;
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
                _uiContext.Post(_ => CloseIfNotClosing(), null);
            }
        };
        PointerExited += (_, _) => StartTiltReset();
    }

    /// <summary>
    /// Win7 等拿不到 Avalonia 透明窗口的 Windows 上，改用 Win32 分层窗口按逐像素 alpha 合成；
    /// Win10/11（DWM/DComp）和 Linux（合成器）原生透明可用时完全不走这里。
    /// 诊断开关：FILESHARE_ABOUT_LAYERED=1 强制启用，=0 强制关闭。
    /// </summary>
    private bool TryEnableLayeredMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var forced = Environment.GetEnvironmentVariable("FILESHARE_ABOUT_LAYERED");
        if (forced == "0")
        {
            return false;
        }

        if (forced != "1" && ActualTransparencyLevel == WindowTransparencyLevel.Transparent)
        {
            return false;
        }

        var renderer = new LayeredWindowRenderer(this, _logger);
        if (!renderer.TryEnable())
        {
            renderer.Dispose();
            _logger.LogWarning("分层窗口不可用，回退为不透明卡片");
            return false;
        }

        _layeredRenderer = renderer;
        _logger.LogInformation(
            "已启用分层窗口逐像素透明（ActualTransparencyLevel={Level}）",
            ActualTransparencyLevel);
        return true;
    }

    /// <summary>
    /// Win7 及更早系统（或任何无法提供窗口级透明的平台）上，TransparencyLevelHint 会退化，
    /// 窗口底色的白色便从圆角卡片四周露出来，表现为「外延一圈白边」。
    /// 这种情况直接让窗口本体充当不透明卡片：同色背景 + 去掉四周留白与圆角，白边自然消失。
    /// </summary>
    private void ApplyOpaqueCardFallbackIfNeeded()
    {
        // 平台确实拿不到透明窗口时才降级（Win7 上属性会退化为 None）
        if (ActualTransparencyLevel == WindowTransparencyLevel.Transparent)
        {
            return;
        }

        TransparencyLevelHint = new[] { WindowTransparencyLevel.None };

        try
        {
            Background = Avalonia.Controls.ResourceNodeExtensions.TryFindResource(
                this, "FsSurfaceBrush", out var brush) && brush is IBrush resourceBrush
                ? resourceBrush
                : Brushes.White;
        }
        catch (Exception)
        {
            Background = Brushes.White;
        }

        TiltRoot.Margin = new Thickness(0);
        TiltRoot.CornerRadius = new CornerRadius(0);
        // 阴影在非透明窗口上会被裁掉，留着只会在卡片边缘留下脏边
        TiltRoot.ClearValue(Border.BoxShadowProperty);
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
    /// 按真实经过时间推进 Logo 角度；转速用指数缓动逼近目标（悬停 0 / 常态 1），
    /// 与帧率无关，掉帧也不会抖；角度取模 360，长时间运行不会有浮点精度漂移。
    /// </summary>
    private void OnLogoTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var elapsed = (now - _logoRotationLastTicks) / 1000.0;
        _logoRotationLastTicks = now;

        // 窗口从后台恢复 / 调试暂停后跳过的巨大步进不参与推进，避免图标瞬移
        if (elapsed <= 0 || elapsed > 0.25)
        {
            return;
        }

        var target = _logoHovered ? 0.0 : 1.0;
        var ease = 1.0 - Math.Exp(-elapsed / LogoRotationEaseSeconds);
        _logoRotationSpeed += (target - _logoRotationSpeed) * ease;

        // 足够接近目标就吸附：悬停时真正停住，移开后回到满速
        if (Math.Abs(_logoRotationSpeed - target) < 0.001)
        {
            _logoRotationSpeed = target;
        }

        if (_logoRotationSpeed <= 0)
        {
            return;
        }

        _logoRotationAngle =
            (_logoRotationAngle + LogoRotationDegreesPerSecond * _logoRotationSpeed * elapsed) % 360.0;
        _logoRotation.Angle = _logoRotationAngle;
        _layeredRenderer?.RequestUpdate();
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
        _layeredRenderer?.RequestUpdate();
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
                    _layeredRenderer?.RequestUpdate();
                    _tiltResetTimer.Stop();
                    return;
                }

                _tiltTransform.AngleX = x * 0.8;
                _tiltTransform.AngleY = y * 0.8;
                _layeredRenderer?.RequestUpdate();
            };
        }

        _tiltResetTimer.Start();
    }
}
