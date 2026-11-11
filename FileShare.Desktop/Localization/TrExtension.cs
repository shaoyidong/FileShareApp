using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;

namespace FileShare.Desktop.Localization;


/// <summary>
/// XAML 标记扩展：<c>Text="{loc:Tr '发送文件'}"</c>。
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string text)
    {
        Text = text;
    }

    /// <summary>中文原文（同时作为翻译 key）。</summary>
    public string? Text { get; set; }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Loc.Get(Text);
    }
}
