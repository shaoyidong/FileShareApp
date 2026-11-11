using Avalonia.Styling;
using FileShare.Desktop.Themes;
using System.Text.Json;

namespace FileShare.Desktop.Tests.Theming;

/// <summary>
/// 运行时防线：直接实例化编译期 XAML 生成的三套皮肤，检查同一个 Style 内
/// 解析后的 <see cref="Setter.Property"/> 是否有重复。
/// <para>
/// 「Duplicate setter encountered for property 'Foreground' in 'ToolTip'」的根因是
/// <c>Foreground</c> 与 <c>TextElement.Foreground</c> 在 XAML 里写法不同，
/// 但 Avalonia 解析后是同一个 AvaloniaProperty；StyleInstance.Add 遇到重复属性就抛异常，
/// ToolTip 一打开就崩，所有 tip 都显示不出来。
/// 检查解析后的属性对象（而不是 XAML 文本）才能真正复现这个判定。
/// </para>
/// </summary>
public class ToolTipRuntimeStyleTests
{
    [Fact]
    public void CompiledSkins_HaveNoDuplicateSetterProperty()
    {
        Styles[] skins = [new Win7Skin(), new WinXPSkin(), new WinXPOliveSkin()];

        foreach (var skin in skins)
        {
            Assert.True(skin.Count > 0, $"{skin.GetType().Name} 未加载出任何样式");

            foreach (var style in skin.OfType<Style>())
            {
                var properties = style.Setters
                    .OfType<Setter>()
                    .Where(s => s.Property is not null)
                    .Select(s => s.Property!)
                    .ToList();

                var duplicates = properties
                    .GroupBy(p => p)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key.Name)
                    .ToList();

                Assert.True(duplicates.Count == 0,
                    $"{skin.GetType().Name} 的 Style [{style.Selector}] 存在重复 Setter 属性: " +
                    $"{string.Join(", ", duplicates)}；ToolTip 打开时会抛 Duplicate setter 异常");
            }
        }
    }

    [Fact]
    public void CompiledSkins_DefineToolTipStyle()
    {
        Styles[] skins = [new Win7Skin(), new WinXPSkin(), new WinXPOliveSkin()];

        foreach (var skin in skins)
        {
            var tooltipStyles = skin.OfType<Style>()
                .Where(s => s.Selector?.ToString()?.Contains("ToolTip", StringComparison.Ordinal) == true)
                .ToList();

            Assert.True(tooltipStyles.Count >= 2,
                $"{skin.GetType().Name} 应同时定义 ToolTip 本体与 ToolTip TextBlock 前景色样式，" +
                $"实际 {tooltipStyles.Count} 条");
        }
    }

    /// <summary>
    /// XP 经典蓝专属缺陷：工具栏是蓝底白字（<c>Border.toolbar TextBlock → White</c>），
    /// 而 ToolTip 的 Popup 逻辑父正是工具栏里的按钮，这条后代选择器会命中 tip 内部文字，
    /// 把浅黄底上的提示刷成白字，几乎不可读。
    /// 修复方式是在 WinXP.axaml 里加一条特异性更高的
    /// <c>Border.toolbar ToolTip TextBlock → FsTooltipTextBrush</c> 规则。
    /// </summary>
    [Fact]
    public void WinXp_ToolbarToolTipText_IsOverriddenBackToDarkInk()
    {
        var xp = new WinXPSkin();
        var styles = xp.OfType<Style>().ToList();

        var whiteToolbarText = styles.FirstOrDefault(s =>
            s.Selector?.ToString() == "Border.toolbar TextBlock");
        Assert.NotNull(whiteToolbarText);
        Assert.True(whiteToolbarText!.Setters.OfType<Setter>().Any(s =>
                s.Property?.Name == "Foreground" &&
                s.Value?.ToString()?.Contains("White", StringComparison.OrdinalIgnoreCase) == true),
            "XP 工具栏白字观感应保留");

        // 补救规则必须存在、特异性更高（多一级 ToolTip 限定）、且指向深色文字资源
        var fix = styles.FirstOrDefault(s =>
            s.Selector?.ToString() == "Border.toolbar ToolTip TextBlock");
        Assert.NotNull(fix);
        Assert.True(fix!.Setters.OfType<Setter>().Any(s =>
                s.Property?.Name == "Foreground" &&
                s.Value is Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension),
            "补救规则应复用 FsTooltipTextBrush，保持主题包可配色");

        // 补救规则必须排在白字规则之后，同层级下后应用者生效
        Assert.True(styles.IndexOf(fix) > styles.IndexOf(whiteToolbarText),
            "补救规则必须晚于工具栏白字规则，否则会被覆盖回去");
    }

    /// <summary>补救规则只属于 XP 基座，Win7 / Olive 不应被改动。</summary>
    [Fact]
    public void OtherSkins_AreNotAffectedByXpToolbarFix()
    {
        foreach (var skin in new Styles[] { new Win7Skin(), new WinXPOliveSkin() })
        {
            var selectors = skin.OfType<Style>()
                .Select(s => s.Selector?.ToString())
                .ToList();

            Assert.DoesNotContain("Border.toolbar ToolTip TextBlock", selectors);
            Assert.DoesNotContain("Border.toolbar TextBlock", selectors);
        }
    }

    /// <summary>三套主题的 tip 前景/背景都必须达到 WCAG AA（≥ 4.5:1）。</summary>
    [Theory]
    [InlineData("win7")]
    [InlineData("winxp")]
    [InlineData("winxp-olive")]
    public void ThemePacks_ToolTipContrast_MeetsWcagAA(string id)
    {
        var path = ResolvePackPath(id + ".json");
        Assert.True(File.Exists(path), $"找不到主题包: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var colors = document.RootElement.GetProperty("colors");
        var background = colors.GetProperty("FsTooltipBrush").GetString()!;
        var foreground = colors.GetProperty("FsTooltipTextBrush").GetString()!;

        var ratio = ContrastRatio(background, foreground);
        Assert.True(ratio >= 4.5,
            $"{id} 的 tip 对比度只有 {ratio:F2}:1（背景 {background} / 文字 {foreground}），低于 WCAG AA 4.5:1");
    }

    private static double ContrastRatio(string background, string foreground) =>
        Contrast(RelativeLuminance(background), RelativeLuminance(foreground));

    private static double Contrast(double a, double b) =>
        (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private static double RelativeLuminance(string hex)
    {
        var value = hex.TrimStart('#');
        double Channel(int offset)
        {
            var c = Convert.ToInt32(value.Substring(offset, 2), 16) / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static string ResolvePackPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "FileShare.Desktop", "Themes", "packs", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return Path.Combine("FileShare.Desktop", "Themes", "packs", fileName);
    }
}
