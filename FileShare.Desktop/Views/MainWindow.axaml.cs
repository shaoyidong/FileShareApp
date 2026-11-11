using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using FileShare.Desktop.Services;
using System;
using System.ComponentModel;
using System.Linq;

namespace FileShare.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly TrayService? _trayService;
    private readonly IUiPreferencesService? _uiPreferences;

    /// <summary>设计器 / XAML 预览使用的无参构造。</summary>
    public MainWindow() : this(null)
    {
    }

    /// <summary>运行时构造：托盘偏好由 DI 注入（属于桌面端 UI 设置，不再来自 Core）。</summary>
    public MainWindow(IUiPreferencesService? uiPreferences)
    {
        InitializeComponent();

        _trayService = App.TrayService;
        _uiPreferences = uiPreferences;

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 托盘图标只在启用了「最小化到托盘 / 关闭到托盘」时显示
        var preferences = _uiPreferences?.Current;
        _trayService?.Attach(this);
        _trayService?.SetVisible(preferences is { MinimizeToTray: true } || preferences is { CloseToTray: true });

        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty || WindowState != WindowState.Minimized)
        {
            return;
        }

        if (_uiPreferences?.Current.MinimizeToTray == true && _trayService is { IsAvailable: true })
        {
            CloseOpenFlyouts(this);
            Hide();
        }
    }

    /// <summary>
    /// 窗口隐藏到托盘前收起所有已打开的 Flyout / Popup。
    /// <para>MenuFlyout 挂在 OverlayLayer 的独立 Popup 上，窗口隐藏后其 PlatformImpl
    /// 会被释放，残留的浮层再触发点击 / 关闭事件会抛异常导致关窗崩溃。</para>
    /// </summary>
    private static void CloseOpenFlyouts(Window window)
    {
        try
        {
            var descendants = window.GetVisualDescendants().ToArray();
            foreach (var control in descendants.OfType<Control>())
            {
                if (control.ContextFlyout is PopupFlyoutBase { IsOpen: true } contextFlyout)
                {
                    contextFlyout.Hide();
                }

                if (control is Button { Flyout: PopupFlyoutBase { IsOpen: true } buttonFlyout })
                {
                    buttonFlyout.Hide();
                }
            }

            foreach (var popup in descendants.OfType<Popup>())
            {
                if (popup.IsOpen)
                {
                    popup.IsOpen = false;
                }
            }
        }
        catch (Exception)
        {
            // 收起浮层失败不应阻断隐藏窗口
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_trayService is null || _uiPreferences?.Current.CloseToTray != true)
        {
            return;
        }

        // 托盘「退出」会先把 IsExiting 置位，此时必须真正关闭
        if (_trayService.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        CloseOpenFlyouts(this);
        Hide();
        _trayService.SetVisible(true);
    }
}
