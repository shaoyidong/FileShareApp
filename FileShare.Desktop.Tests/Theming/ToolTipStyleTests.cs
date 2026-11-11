using System.Text.RegularExpressions;

namespace FileShare.Desktop.Tests.Theming;

/// <summary>
/// ToolTip 崩溃回归：Avalonia 里 <c>Foreground</c>、<c>TextElement.Foreground</c>、
/// <c>TextBlock.Foreground</c> 会解析成同一个 AvaloniaProperty。同一个 Style 中出现
/// 两个这样的 Setter，会在 ToolTip 打开时抛
/// "Duplicate setter encountered for property 'Foreground' in 'ToolTip'"，
/// 表现为「所有 tip 都显示不出来」，且异常以 UnobservedTaskException 刷屏。
/// </summary>
public class ToolTipStyleTests
{
    private static readonly string[] ThemeFiles = ["Win7.axaml", "WinXP.axaml", "WinXPOlive.axaml"];

    [Theory]
    [InlineData("Win7.axaml")]
    [InlineData("WinXP.axaml")]
    [InlineData("WinXPOlive.axaml")]
    public void Theme_HasNoDuplicateForegroundSetter(string fileName)
    {
        var path = ResolveThemePath(fileName);
        Assert.True(File.Exists(path), $"找不到主题文件: {path}");

        var xaml = File.ReadAllText(path);

        foreach (Match style in Regex.Matches(xaml, @"<Style\b[^>]*>[\s\S]*?</Style>"))
        {
            var setters = Regex.Matches(style.Value, @"<Setter\s+Property=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .Where(p => p.EndsWith("Foreground", StringComparison.Ordinal))
                .ToList();

            Assert.True(setters.Count <= 1,
                $"{fileName} 的同一 Style 内出现了 {setters.Count} 个 Foreground 类 Setter " +
                $"({string.Join(", ", setters)})，ToolTip 打开时会抛 Duplicate setter 异常。\n{style.Value}");
        }
    }

    [Fact]
    public void AllThemes_DefineToolTipStyle()
    {
        foreach (var fileName in ThemeFiles)
        {
            var xaml = File.ReadAllText(ResolveThemePath(fileName));
            Assert.True(xaml.Contains("Selector=\"ToolTip\"", StringComparison.Ordinal),
                $"{fileName} 缺少 ToolTip 样式");
            Assert.True(xaml.Contains("Selector=\"ToolTip TextBlock\"", StringComparison.Ordinal),
                $"{fileName} 缺少 ToolTip 内部 TextBlock 前景色样式");
        }
    }

    private static string ResolveThemePath(string fileName)
    {
        // 测试运行目录可能是 bin/Debug/net10.0，从它向上找解决方案根
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "FileShare.Desktop", "Themes", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine("FileShare.Desktop", "Themes", fileName);
    }
}
