using System.Collections.Generic;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 简体中文（源语言）：Map 为空，所有文案原样返回中文原文。
/// 注册它是为了让语言下拉框能显示「简体中文」。
/// </summary>
public sealed class ChineseLanguagePack : ILanguagePack
{
    public string Code => "zh-CN";

    public string DisplayName => "简体中文";

    public IReadOnlyDictionary<string, string> Map { get; } =
        new Dictionary<string, string>();
}
