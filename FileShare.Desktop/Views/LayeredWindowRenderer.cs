using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace FileShare.Desktop.Views;

/// <summary>
/// Win7 等拿不到 Avalonia 透明窗口的 Windows 上，用 WS_EX_LAYERED + UpdateLayeredWindow
/// 把 Avalonia 渲染出的整窗位图按逐像素 alpha 合成到桌面。
/// 圆角、四周留白、BoxShadow 和 RenderTransform 动画都保留；30fps 节流，只有脏帧才上传。
/// </summary>
internal sealed partial class LayeredWindowRenderer : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;
    private const int FrameIntervalMs = 33;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(FrameIntervalMs);

    private readonly Window _window;
    private readonly ILogger _logger;
    private IntPtr _hwnd;
    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _previousBitmap;
    private RenderTargetBitmap? _bitmap;
    private PixelSize _pixelSize;
    private double _scaling;
    private DispatcherTimer? _timer;
    private bool _dirty;
    private bool _hasPresented;
    private bool _loggedRgba;
    private bool _disposed;

    public LayeredWindowRenderer(Window window, ILogger logger)
    {
        _window = window;
        _logger = logger;
    }

    public bool TryEnable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        _hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hwnd == IntPtr.Zero)
            return false;

        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        // 与 DirectComposition/WinUI 合成的 NOREDIRECTIONBITMAP 互斥，命中说明不是 Win7 路径
        if ((exStyle & WS_EX_NOREDIRECTIONBITMAP) != 0)
        {
            _logger.LogInformation("窗口使用 NOREDIRECTIONBITMAP，跳过分层窗口合成");
            return false;
        }

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            Dispose();
            return false;
        }

        _memoryDc = CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero)
        {
            Dispose();
            return false;
        }

        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);

        if (!Update())
        {
            Dispose();
            return false;
        }

        _hasPresented = true;
        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += (_, _) =>
        {
            if (!_dirty)
                return;

            _dirty = false;
            if (!Update())
            {
                _timer?.Stop();
            }
        };
        _timer.Start();
        return true;
    }

    /// <summary>标记画面已变化；由下一次 33ms 定时器节流上传。</summary>
    public void RequestUpdate() => _dirty = true;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer?.Stop();
        _timer = null;

        ReleaseSurface();

        if (_memoryDc != IntPtr.Zero)
        {
            DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        int result = ReleaseDC(IntPtr.Zero, _screenDc);
        if (result != 0)
        {
            _screenDc = IntPtr.Zero;
        }
        // result == 0 时保留 _screenDc，让后续逻辑（如调试诊断）有机会察觉

        // 第一次上传前失败（准备回退到不透明卡片）时要撤掉分层样式，否则窗口会一直不可见
        if (!_hasPresented && _hwnd != IntPtr.Zero)
        {
            var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
        }
    }

    private bool Update()
    {
        if (_disposed || !EnsureSurface())
            return false;

        try
        {
            _bitmap!.Render(_window);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分层窗口渲染失败");
            return false;
        }

        var stride = _pixelSize.Width * 4;
        var byteCount = stride * _pixelSize.Height;

        try
        {
            _bitmap.CopyPixels(
                new PixelRect(0, 0, _pixelSize.Width, _pixelSize.Height),
                _dibBits,
                byteCount,
                stride);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分层窗口位图拷贝失败");
            return false;
        }

        // UpdateLayeredWindow 要求 BGRA premultiplied；Skia 在非 Windows 上可能给 RGBA
        if (_bitmap.Format == PixelFormats.Rgba8888)
        {
            SwapRedBlue(_dibBits, byteCount);
            if (!_loggedRgba)
            {
                _loggedRgba = true;
                _logger.LogInformation("分层窗口像素格式为 Rgba8888，已转为 Bgra8888");
            }
        }
        else if (_bitmap.Format is { } format && format != PixelFormat.Bgra8888)
        {
            _logger.LogWarning("不支持的分层窗口像素格式 {Format}", format);
            return false;
        }

        if (_bitmap.AlphaFormat is { } alphaFormat && alphaFormat != AlphaFormat.Premul)
        {
            _logger.LogWarning("不支持的分层窗口 Alpha 格式 {AlphaFormat}", alphaFormat);
            return false;
        }

        if (!GetWindowRect(_hwnd, out var windowRect))
            return false;

        var dst = new POINT { X = windowRect.Left, Y = windowRect.Top };
        var size = new SIZE { Cx = _pixelSize.Width, Cy = _pixelSize.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        if (!UpdateLayeredWindow(_hwnd, _screenDc, ref dst, ref size, _memoryDc, ref src, 0, ref blend, ULW_ALPHA))
        {
            _logger.LogWarning("UpdateLayeredWindow 失败，Win32Error={Error}", Marshal.GetLastWin32Error());
            return false;
        }

        return true;
    }

    private bool EnsureSurface()
    {
        if (!GetClientRect(_hwnd, out var clientRect))
            return false;

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
            return false;

        var scaling = _window.RenderScaling;
        if (scaling <= 0)
            return false;

        var size = new PixelSize(width, height);
        if (_bitmap != null && _pixelSize == size && Math.Abs(_scaling - scaling) < 0.001)
            return true;

        ReleaseSurface();

        _bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        _pixelSize = size;
        _scaling = scaling;

        var bmi = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BI_RGB
            }
        };

        _dib = CreateDIBSection(_screenDc, ref bmi, DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _dibBits == IntPtr.Zero)
        {
            _logger.LogWarning("CreateDIBSection 失败，Win32Error={Error}", Marshal.GetLastWin32Error());
            return false;
        }

        var previousBitmap = SelectObject(_memoryDc, _dib);
        if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
        {
            _logger.LogWarning("SelectObject 选入 DIB 失败，Win32Error={Error}", Marshal.GetLastWin32Error());
            return false;
        }

        _previousBitmap = previousBitmap;
        return true;
    }

    private void ReleaseSurface()
    {
        _bitmap?.Dispose();
        _bitmap = null;

        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
        {
            SelectObject(_memoryDc, _previousBitmap);
            _previousBitmap = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
            _dib = IntPtr.Zero;
            _dibBits = IntPtr.Zero;
        }
    }

    private static unsafe void SwapRedBlue(IntPtr bits, int byteCount)
    {
        var p = (byte*)bits;
        for (var i = 0; i < byteCount; i += 4)
        {
            (p[i], p[i + 2]) = (p[i + 2], p[i]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Colors;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(
        IntPtr hWnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr ho);
}
