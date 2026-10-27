using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Services;

/// <summary>
/// 轻量「系统通知」：在工作区右下角弹出一个自绘小卡片，几秒后自动消失，点击可回到主窗口。
/// <para>不依赖平台 Toast API（Windows/macOS/Linux 差异大且部分环境不可用），
/// 因此跨平台行为一致，并且能继承当前皮肤。</para>
/// </summary>
public interface IToastService
{
    /// <summary>显示一条通知（新的会替换旧的）。</summary>
    void Show(string title, string message, Action? onClick = null);
}

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

        CloseCurrent();

        var owner = _ownerProvider();
        var toast = new Views.ToastWindow(title, message, onClick);
        _current = toast;

        // 位置：主屏工作区右下角
        var screen = owner?.Screens.ScreenFromWindow(owner) ?? owner?.Screens.Primary;
        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);

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
