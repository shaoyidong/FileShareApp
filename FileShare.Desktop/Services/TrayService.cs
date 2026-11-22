using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.IO;
using Shyd.Prism.Localization;
using FileShare.Desktop.Helpers;

namespace FileShare.Desktop.Services;

// 接口定义已拆到 ITrayService.cs。

/// <inheritdoc cref="ITrayService"/>
public sealed class TrayService : ITrayService
{ 
    private readonly Application _application;
    private readonly ILogger<TrayService> _logger;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private Window? _window;

    public TrayService(Application application)
        : this(application, NullLogger<TrayService>.Instance)
    {
    }

    public TrayService(Application application, ILogger<TrayService> logger)
    {        
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _logger = logger ?? NullLogger<TrayService>.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Linux 上光「创建出 TrayIcon 对象」不代表托盘真的能用：裸环境（WSL、没有桌面面板的
    /// 极简环境）里 Avalonia 只会打一条
    /// <c>Interface 'org.kde.StatusNotifierWatcher' is unavailable</c>，
    /// 图标永远不会出现，但对象创建是成功的。此时若按「可用」处理，
    /// 「关闭到托盘」会把主窗口藏进一个看不见的图标里，用户再也没有入口把它叫回来。
    /// 因此这里用 XDG_CURRENT_DESKTOP 做保守判定：没有桌面标识就当托盘不可用，
    /// 宁可让关闭真正退出（还能重新启动），也不要把窗口藏丢。
    /// </remarks>
    public bool IsAvailable => _trayIcon is not null && IsTrayHostLikelyPresent();

    private static bool IsTrayHostLikelyPresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));
    }

    /// <inheritdoc />
    public bool IsExiting { get; set; }

    /// <inheritdoc />
    public void Attach(Window window)
    {
        if (_trayIcon is not null || window is null)
        {
            return;
        }

        // Linux 上没有桌面面板的环境（WSL、极简 WM）里不创建托盘图标：
        // Avalonia 建 TrayIcon 时会去连 D-Bus 的 org.kde.StatusNotifierWatcher，
        // 连不上就抛异常并往日志里刷一段 Error + 堆栈；而图标本来也永远不会显示。
        // 跳过创建既消除噪音，也避免"关闭到托盘"把窗口藏进一个看不见的图标里。
        if (!IsTrayHostLikelyPresent())
        {
            _logger.LogInformation(
                "未检测到桌面托盘宿主（XDG_CURRENT_DESKTOP 为空），已跳过托盘图标，关闭窗口将直接退出");
            return;
        }

        _window = window;

        try
        {
            var icon = AppIconLoader.LoadWindowIcon();
            if (icon is null)
            {
                return;
            }

            var menu = new NativeMenu();

            var showItem = new NativeMenuItem { Header = Loc.Get("显示主窗口") };
            showItem.Click += (_, _) => ShowMainWindow();

            var hideItem = new NativeMenuItem { Header = Loc.Get("隐藏到托盘") };
            hideItem.Click += (_, _) => HideMainWindow();

            var exitItem = new NativeMenuItem { Header = Loc.Get("退出") };
            exitItem.Click += (_, _) => Exit();

            menu.Add(showItem);
            menu.Add(hideItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = Loc.Get("FileShare 局域网传输"),
                Menu = menu,
                IsVisible = false
            };
            _trayIcon.Clicked += (_, _) => ShowMainWindow();

            _trayIcons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(_application, _trayIcons);
        }
        catch (Exception)
        {
            // 托盘在某些 Linux 桌面环境/无面板环境下不可用，属于可接受的降级
            _trayIcon = null;
            _trayIcons = null;
        }
    }

    /// <inheritdoc />
    public void SetVisible(bool visible)
    {
        _trayIcon?.IsVisible = visible;
    }

    /// <inheritdoc />
    public void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <inheritdoc />
    public void HideMainWindow()
    {
        _window?.Hide();
    }

    /// <inheritdoc />
    public void Exit()
    {
        IsExiting = true;

        _trayIcon?.IsVisible = false;

        // Hide 之后再 Shutdown，避免关闭确认逻辑把应用又藏起来
        _window?.Hide();

        if (_application.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime
            && desktopLifetime is not null)
            desktopLifetime.Shutdown();

    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayIcons = null;
    }
}
