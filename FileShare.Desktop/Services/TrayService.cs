using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using System;
using System.IO;
using FileShare.Desktop.Localization;
using FileShare.Desktop.Helpers;

namespace FileShare.Desktop.Services;

// 接口定义已拆到 ITrayService.cs。

/// <inheritdoc cref="ITrayService"/>
public sealed class TrayService : ITrayService
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private Window? _window;

    public TrayService(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    /// <inheritdoc />
    public bool IsAvailable => _trayIcon is not null;

    /// <inheritdoc />
    public bool IsExiting { get; set; }

    /// <inheritdoc />
    public void Attach(Window window)
    {
        if (_trayIcon is not null || window is null)
        {
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
            if (Application.Current is { } application)
            {
                TrayIcon.SetIcons(application, _trayIcons);
            }
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
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = visible;
        }
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

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
        }

        // Hide 之后再 Shutdown，避免关闭确认逻辑把应用又藏起来
        if (_window is not null)
        {
            _window.Hide();
        }

        _lifetime.Shutdown();
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
