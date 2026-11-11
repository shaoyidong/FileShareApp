using System.Collections.Generic;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 语言包：新增一种语言 = 在「应用数据目录 / i18n」放一个 JSON 文件（由
/// <see cref="LanguagePackLoader"/> 加载并注册到 <see cref="Loc"/>），无需改代码或重新编译。
/// <para>
/// 译文表的 key 仍是中文原文，因此界面上几百个 <c>Loc.Get("中文")</c> 调用点零改动；
/// 查不到译文时 <see cref="Loc.Get"/> 自动回退中文原文。
/// </para>
/// </summary>
public interface ILanguagePack
{
    /// <summary>BCP-47 语言代码，例如 <c>zh-CN</c> / <c>en-US</c>。</summary>
    string Code { get; }

    /// <summary>语言下拉框里的展示名称（用该语言自身的文字）。</summary>
    string DisplayName { get; }

    /// <summary>中文原文 → 译文；为空表示源语言（中文）本身。</summary>
    IReadOnlyDictionary<string, string> Map { get; }
}
