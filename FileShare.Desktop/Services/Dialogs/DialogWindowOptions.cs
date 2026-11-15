using Avalonia.Controls;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 宿主窗口显示配置（内容弹窗作用于宿主窗口，窗口弹窗作用于弹窗自身）。
/// <para>全部属性可空：只应用显式赋值的项，宿主窗口 / 窗口 XAML 的默认值不受影响。
/// 注意设置 Width / Height 后如需固定尺寸，通常需同时把 <see cref="SizeToContent"/> 设为 Manual。</para>
/// </summary>
public sealed class DialogWindowOptions
{
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? MinWidth { get; set; }
    public double? MinHeight { get; set; }
    public double? MaxWidth { get; set; }
    public double? MaxHeight { get; set; }

    /// <summary>是否允许用户拖拽调整大小。</summary>
    public bool? CanResize { get; set; }

    public bool? ShowInTaskbar { get; set; }

    /// <summary>是否以激活方式显示（false = 不抢焦点，配合非模态弹窗）。</summary>
    public bool? ShowActivated { get; set; }

    public bool? Topmost { get; set; }

    public SizeToContent? SizeToContent { get; set; }

    /// <summary>窗口装饰（无边框卡片等自定义 chrome）。</summary>
    public SystemDecorations? SystemDecorations { get; set; }

    public WindowStartupLocation? WindowStartupLocation { get; set; }

    /// <summary>把显式赋值的属性应用到窗口（未赋值的项保持窗口当前值）。</summary>
    public void ApplyTo(Window window)
    {
        if (Width is { } width)
        {
            window.Width = width;
        }

        if (Height is { } height)
        {
            window.Height = height;
        }

        if (MinWidth is { } minWidth)
        {
            window.MinWidth = minWidth;
        }

        if (MinHeight is { } minHeight)
        {
            window.MinHeight = minHeight;
        }

        if (MaxWidth is { } maxWidth)
        {
            window.MaxWidth = maxWidth;
        }

        if (MaxHeight is { } maxHeight)
        {
            window.MaxHeight = maxHeight;
        }

        if (CanResize is { } canResize)
        {
            window.CanResize = canResize;
        }

        if (ShowInTaskbar is { } showInTaskbar)
        {
            window.ShowInTaskbar = showInTaskbar;
        }

        if (ShowActivated is { } showActivated)
        {
            window.ShowActivated = showActivated;
        }

        if (Topmost is { } topmost)
        {
            window.Topmost = topmost;
        }

        if (SizeToContent is { } sizeToContent)
        {
            window.SizeToContent = sizeToContent;
        }

        if (SystemDecorations is { } systemDecorations)
        {
            window.SystemDecorations = systemDecorations;
        }

        if (WindowStartupLocation is { } startupLocation)
        {
            window.WindowStartupLocation = startupLocation;
        }
    }
}
