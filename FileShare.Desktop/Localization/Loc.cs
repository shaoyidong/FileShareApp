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
/// 译文存放在「应用数据目录 / i18n / *.json」：改 JSON 文件即可改译文，按同样格式放入新文件
/// 即可新增一套语言，语言下拉框会自动多出一项。首次访问会自动加载（无需显式初始化），
/// AOT / TrimMode=full 下也安全（只用 JsonDocument 与文件 IO，不用反射序列化）。
/// </para>
/// </summary>
public static class Loc
{
    private const string DefaultCode = "zh-CN";

    private static readonly List<ILanguagePack> _packs = new();
    private static readonly Dictionary<string, ILanguagePack> _byCode =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _initGate = new();
    private static bool _filePacksLoaded;
    private static ILanguagePack _current = null!;

    static Loc()
    {
        // 源语言（中文）内置保底；其它语言由 i18n/*.json 在启动时动态注册
        Register(new ChineseLanguagePack());
        _current = _byCode[DefaultCode];
    }

    /// <summary>
    /// 从外部语言包文件初始化。新增语言只需在「数据目录 / i18n」放入一个符合格式的 JSON 文件，
    /// 不需要改代码、也不需要重新编译（AOT 发布后同样适用）。
    /// </summary>
    public static void Initialize(string dataDirectory)
    {
        LanguagePackLoader.Initialize(dataDirectory);
        _filePacksLoaded = true;
    }

    /// <summary>懒加载语言包文件（测试 / 设计器等未显式调用 Initialize 的场景也能正常工作）。</summary>
    private static void EnsurePacksLoaded()
    {
        if (_filePacksLoaded)
        {
            return;
        }

        lock (_initGate)
        {
            if (_filePacksLoaded)
            {
                return;
            }

            LanguagePackLoader.Initialize(
                global::FileShare.Core.Services.AppPaths.GetDataDirectory());
            _filePacksLoaded = true;
        }
    }

    /// <summary>重新读取语言包文件（发布后直接改文件 / 新增语言文件后可即时生效）。</summary>
    public static int ReloadPacks()
    {
        EnsurePacksLoaded();
        var loaded = LanguagePackLoader.Reload();

        // 当前语言包可能已被替换，回退到中文并通知界面重建
        if (!_byCode.ContainsKey(_current.Code))
        {
            _current = _byCode[DefaultCode];
        }
        LanguageChanged?.Invoke();

        return loaded;
    }

    /// <summary>已注册的语言包（按注册顺序，用于生成语言下拉框）。</summary>
    public static IReadOnlyList<ILanguagePack> Packs
    {
        get
        {
            EnsurePacksLoaded();
            return _packs.AsReadOnly();
        }
    }

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
            if (ReferenceEquals(_current, existing))
            {
                _current = pack;
            }
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
        EnsurePacksLoaded();

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

        EnsurePacksLoaded();
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
