using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 界面语言。
/// </summary>
public enum AppLanguage
{
    /// <summary>简体中文（默认）。</summary>
    ZhCN = 0,

    /// <summary>English。</summary>
    EnUS = 1,
}

/// <summary>
/// 轻量本地化。
/// <para>**以中文原文作为 key**：中文侧不需要任何字典（原样返回），只需维护一张「中 → 英」映射表。
/// 这样迁移成本最低，且不会出现「key 写错导致界面显示 key」的问题（找不到译文时自动回退中文）。</para>
/// </summary>
public static class Loc
{
    private static AppLanguage _language = AppLanguage.ZhCN;

    /// <summary>当前语言。</summary>
    public static AppLanguage Language => _language;

    /// <summary>语言切换事件（切换后需要重建界面才能生效）。</summary>
    public static event Action? LanguageChanged;

    /// <summary>设置语言。</summary>
    public static void SetLanguage(AppLanguage language)
    {
        if (_language == language)
        {
            return;
        }

        _language = language;
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// 取译文：英文且存在映射时返回英文，否则返回中文原文。
    /// </summary>
    public static string Get(string? chinese)
    {
        if (string.IsNullOrEmpty(chinese))
        {
            return string.Empty;
        }

        if (_language == AppLanguage.EnUS && EnglishTranslations.Map.TryGetValue(chinese, out var english))
        {
            return english;
        }

        return chinese;
    }

    /// <summary>
    /// 带参数的译文：模板里用 <c>{0}</c>、<c>{1}</c> 占位。
    /// </summary>
    public static string F(string chineseTemplate, params object?[] args)
    {
        var template = Get(chineseTemplate);

        try
        {
            return args.Length == 0 ? template : string.Format(template, args);
        }
        catch (FormatException)
        {
            // 译文模板占位符与参数不匹配时，退回原始拼接，避免界面崩溃
            return chineseTemplate;
        }
    }
}

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
