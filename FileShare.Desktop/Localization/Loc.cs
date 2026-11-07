using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 轻量本地化（可扩展多语言包）。
/// <para>
/// **以中文原文作为 key**：中文侧不需要任何字典（原样返回），其它语言只需提供一张「中 → 译文」表。
/// 迁移成本最低，且不会出现「key 写错导致界面显示 key」的问题（找不到译文时自动回退中文）。
/// </para>
/// <para>
/// 新增一种语言：新建一个实现 <see cref="ILanguagePack"/> 的文件（一个译文表 + Code/DisplayName），
/// 再在 <see cref="Register"/> 处加一行注册即可；语言下拉框会自动多出一项，
/// 不需要改枚举、分支或 code 转换。
/// </para>
/// </summary>
public static class Loc
{
    private const string DefaultCode = "zh-CN";

    private static readonly List<ILanguagePack> _packs = new();
    private static readonly Dictionary<string, ILanguagePack> _byCode =
        new(StringComparer.OrdinalIgnoreCase);
    private static ILanguagePack _current = null!;

    static Loc()
    {
        // ---- 语言注册表：新增语言只需在这里加一行 ----
        Register(new ChineseLanguagePack());
        Register(new EnglishLanguagePack());
        // -----------------------------------------------

        _current = _byCode[DefaultCode];
    }

    /// <summary>已注册的语言包（按注册顺序，用于生成语言下拉框）。</summary>
    public static IReadOnlyList<ILanguagePack> Packs => _packs.AsReadOnly();

    /// <summary>当前语言的 BCP-47 代码。</summary>
    public static string LanguageCode => _current.Code;

    /// <summary>当前语言（返回 BCP-47 代码；保留该属性名以兼容既有调用点/测试）。</summary>
    public static string Language => _current.Code;

    /// <summary>语言切换事件（切换后需要重建界面才能生效）。</summary>
    public static event Action? LanguageChanged;

    /// <summary>注册（或替换）一个语言包。</summary>
    public static void Register(ILanguagePack pack)
    {
        if (pack is null)
        {
            throw new ArgumentNullException(nameof(pack));
        }

        if (_byCode.TryGetValue(pack.Code, out var existing))
        {
            var index = _packs.IndexOf(existing);
            _packs[index] = pack;
            _byCode[pack.Code] = pack;
        }
        else
        {
            _packs.Add(pack);
            _byCode.Add(pack.Code, pack);
        }
    }

    /// <summary>按 BCP-47 code 切换语言；未注册的 code 回退到简体中文。</summary>
    public static void SetLanguage(string code)
    {
        var normalized = !string.IsNullOrWhiteSpace(code) && _byCode.ContainsKey(code)
            ? code
            : DefaultCode;

        if (string.Equals(_current.Code, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _current = _byCode[normalized];
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// 取译文：当前语言包存在映射时返回译文，否则返回中文原文。
    /// </summary>
    public static string Get(string? chinese)
    {
        if (string.IsNullOrEmpty(chinese))
        {
            return string.Empty;
        }

        return _current.Map.TryGetValue(chinese, out var translated) ? translated : chinese;
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
