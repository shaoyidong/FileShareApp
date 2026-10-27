using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using System;
using System.IO;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.Services;

/// <summary>
/// 系统托盘：常驻图标 + 菜单（显示主窗口 / 退出），配合「最小化到托盘」「关闭到托盘」使用。
/// </summary>
public interface ITrayService : IDisposable
{
    /// <summary>托盘是否可用（图标创建成功）。</summary>
    bool IsAvailable { get; }

    /// <summary>把托盘图标挂到窗口上（每个窗口只需调用一次）。</summary>
    void Attach(Window window);

    /// <summary>显示/隐藏托盘图标。</summary>
    void SetVisible(bool visible);

    /// <summary>从托盘恢复主窗口。</summary>
    void ShowMainWindow();

    /// <summary>隐藏主窗口到托盘。</summary>
    void HideMainWindow();

    /// <summary>是否正在退出（用于让主窗口正确响应关闭）。</summary>
    bool IsExiting { get; set; }

    /// <summary>退出应用。</summary>
    void Exit();
}

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

/// <summary>
/// 从 <c>Assets/logo.ico</c> 里取出 PNG 帧作为托盘图标。
/// <para>SkiaSharp 不解码 .ico 容器，但本项目的 ico 第一帧就是 PNG，直接按 ICO 目录取出该帧即可。</para>
/// </summary>
internal static class AppIconLoader
{
    private const string IconUri = "avares://FileShare.Desktop/Assets/logo.ico";

    public static WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var asset = AssetLoader.Open(new Uri(IconUri));
            using var buffer = new MemoryStream();
            asset.CopyTo(buffer);
            var bytes = buffer.ToArray();

            var pngOffset = FindPngFrame(bytes, out var pngLength);
            if (pngOffset >= 0 && pngLength > 0)
            {
                return new WindowIcon(new MemoryStream(bytes, pngOffset, pngLength, writable: false));
            }

            return new WindowIcon(new MemoryStream(bytes, writable: false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 在 ICO 容器里找到第一个 PNG 帧。
    /// </summary>
    private static int FindPngFrame(byte[] bytes, out int length)
    {
        length = 0;

        if (bytes.Length < 22 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
        {
            return -1;
        }

        var count = BitConverter.ToUInt16(bytes, 4);
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + index * 16;
            if (entry + 16 > bytes.Length)
            {
                break;
            }

            var size = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            if (size <= 8 || offset <= 0 || offset + size > bytes.Length)
            {
                continue;
            }

            if (bytes[offset] == 0x89 && bytes[offset + 1] == 0x50 && bytes[offset + 2] == 0x4E && bytes[offset + 3] == 0x47)
            {
                length = size;
                return offset;
            }
        }

        return -1;
    }
}
