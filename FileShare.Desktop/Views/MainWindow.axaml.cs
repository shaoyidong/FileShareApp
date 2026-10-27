using Avalonia;
using Avalonia.Controls;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.ComponentModel;

namespace FileShare.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly TrayService? _trayService;
    private readonly IAppSettingsService? _settingsService;

    public MainWindow()
    {
        InitializeComponent();

        _trayService = App.TrayService;
        _settingsService = App.ServiceProvider?.GetService<IAppSettingsService>();

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 托盘图标只在启用了「最小化到托盘 / 关闭到托盘」时显示
        var settings = _settingsService?.Current;
        _trayService?.Attach(this);
        _trayService?.SetVisible(settings is { MinimizeToTray: true } || settings is { CloseToTray: true });

        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty || WindowState != WindowState.Minimized)
        {
            return;
        }

        if (_settingsService?.Current.MinimizeToTray == true && _trayService is { IsAvailable: true })
        {
            Hide();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_trayService is null || _settingsService?.Current.CloseToTray != true)
        {
            return;
        }

        // 托盘「退出」会先把 IsExiting 置位，此时必须真正关闭
        if (_trayService.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _trayService.SetVisible(true);
    }
}
