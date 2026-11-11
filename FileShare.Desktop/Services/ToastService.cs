using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;

namespace FileShare.Desktop.Services;

// 接口定义已拆到 IToastService.cs。

/// <inheritdoc cref="IToastService"/>
public sealed class ToastService : IToastService, IDisposable
{
    private const double Margin = 16;
    private const int DefaultDurationMs = 6000;

    private readonly Func<Window?> _ownerProvider;
    private Views.ToastWindow? _current;
    private DispatcherTimer? _timer;

    public ToastService(Func<Window?> ownerProvider)
    {
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
    }

    /// <inheritdoc />
    public void Show(string title, string message, Action? onClick = null)
    {
        if (Dispatcher.UIThread.CheckAccess() is false)
        {
            Dispatcher.UIThread.Post(() => Show(title, message, onClick));
            return;
        }

        var owner = _ownerProvider();

        // 前台不弹通知：主窗口正活跃时用户本来就能看到界面，弹卡片只会打断操作
        if (owner is { IsActive: true })
        {
            return;
        }

        CloseCurrent();

        var toast = new Views.ToastWindow(title, message, onClick);
        _current = toast;

        // 位置：主屏工作区右下角。主窗口隐藏到托盘时 PlatformImpl 可能已释放，
        // 因此不能无条件调用 ScreenFromWindow(owner)，统一走容错解析。
        var workArea = ResolveWorkArea(owner);

        toast.Position = new PixelPoint(
            workArea.X + workArea.Width - (int)toast.Width - (int)Margin,
            workArea.Y + workArea.Height - 220 - (int)Margin);

        toast.Show();

        // 位置依赖实际高度：等布局完成后再校正一次
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_current != toast || toast.Bounds.Height <= 0)
                {
                    return;
                }

                toast.Position = new PixelPoint(
                    workArea.X + workArea.Width - (int)toast.Bounds.Width - (int)Margin,
                    workArea.Y + workArea.Height - (int)toast.Bounds.Height - (int)Margin);
            },
            DispatcherPriority.Background);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DefaultDurationMs) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            CloseCurrent();
        };
        _timer.Start();
    }

    /// <summary>
    /// 解析通知所在屏幕的工作区。
    /// <para>窗口隐藏 / 正在关闭时访问 Screens 会触发 Avalonia 的 "PlatformImpl is null" 警告，
    /// 这里先只在窗口可见时查询，并对所有平台调用做兜底，避免日志噪音与异常。</para>
    /// </summary>
    private static PixelRect ResolveWorkArea(Window? owner)
    {
        try
        {
            if (owner is { IsVisible: true })
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is not null)
                {
                    return screen.WorkingArea;
                }
            }
        }
        catch (Exception)
        {
            // 平台实现已释放，落回下面的主屏 / 默认工作区
        }

        try
        {
            if (owner is not null && owner.Screens.Primary is { } primary)
            {
                return primary.WorkingArea;
            }
        }
        catch (Exception)
        {
            // 同上
        }

        return new PixelRect(0, 0, 1280, 720);
    }

    private void CloseCurrent()
    {
        _timer?.Stop();
        _timer = null;

        if (_current is null)
        {
            return;
        }

        var toast = _current;
        _current = null;

        try
        {
            toast.Close();
        }
        catch (Exception)
        {
            // 关闭失败忽略
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CloseCurrent();
    }
}
