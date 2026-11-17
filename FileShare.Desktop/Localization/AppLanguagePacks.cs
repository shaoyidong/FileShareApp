using Shyd.Prism.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 应用自带的内置语言包种子（嵌入资源 Localization/packs/*.json）。
/// <para>翻译内容由应用层决定：框架（Shyd.Prism.Core）只提供加载 / 种子 / 合并 / 热重载机制，
/// 本类把应用的嵌入资源转成 <see cref="BuiltInLanguagePack"/> 种子交给
/// <see cref="Loc.Initialize(string, IEnumerable{BuiltInLanguagePack}?)"/>。</para>
/// </summary>
public static class AppLanguagePacks
{
    /// <summary>应用内置语言包种子（启动时扫描一次）。</summary>
    public static IReadOnlyList<BuiltInLanguagePack> BuiltIn { get; } = ScanEmbeddedPacks();

    private static List<BuiltInLanguagePack> ScanEmbeddedPacks()
    {
        var packs = new List<BuiltInLanguagePack>();
        try
        {
            var assembly = typeof(AppLanguagePacks).Assembly;
            // 必须用完整 marker：".packs." 会同时命中 Themes/packs/*.json（主题配色包），
            // 导致主题 id 被注册成「语言」出现在界面语言下拉框里
            const string marker = ".Localization.packs.";
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var index = resource.LastIndexOf(marker, StringComparison.Ordinal);
                if (index < 0 || !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = resource[(index + marker.Length)..];
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                using var reader = new StreamReader(stream);
                packs.Add(new BuiltInLanguagePack(fileName, reader.ReadToEnd()));
            }
        }
        catch (Exception)
        {
            // 扫描失败视为无内置包：数据目录里已有的语言包仍可用
        }

        return packs;
    }
}
