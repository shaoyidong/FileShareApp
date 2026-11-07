using System.Collections.Generic;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 语言包：新增一种语言 = 新建一个实现本接口的文件 + 在 <see cref="Loc.Register"/> 注册一行。
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
