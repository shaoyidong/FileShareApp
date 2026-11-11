using System.Collections.Generic;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 由外部 JSON 文件加载的语言包。译文修改后重启应用（或在设置页点「重载语言包」）即可生效。
/// </summary>
public sealed class JsonLanguagePack : ILanguagePack
{
    public JsonLanguagePack(string code, string displayName, IReadOnlyDictionary<string, string> map)
    {
        Code = code;
        DisplayName = displayName;
        Map = map;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public IReadOnlyDictionary<string, string> Map { get; }
}
