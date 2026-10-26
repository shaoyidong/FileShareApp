using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using FileShare.Core.Services;
using System;
using System.Linq;

namespace FileShare.Desktop.Services;

/// <summary>
/// 皮肤管理：运行时在 Windows 7 / Windows XP 两套样式之间切换。
/// </summary>
public interface IThemeService
{
    /// <summary>当前皮肤。</summary>
    AppSkin Current { get; }

    /// <summary>应用皮肤（会替换 <see cref="Application.Styles"/> 中的皮肤 StyleInclude）。</summary>
    void Apply(AppSkin skin);
}

/// <inheritdoc cref="IThemeService"/>
public sealed class ThemeService : IThemeService
{
    private const string AssemblyRoot = "avares://FileShare.Desktop/";

    private static readonly string[] SkinRelativePaths =
    {
        "Themes/Win7.axaml",
        "Themes/WinXP.axaml"
    };

    /// <inheritdoc />
    public AppSkin Current { get; private set; } = AppSkin.Win7;

    /// <inheritdoc />
    public void Apply(AppSkin skin)
    {
        var application = Application.Current;
        if (application is null)
        {
            // 设计器 / 单元测试环境：只记录状态，不做实际切换
            Current = skin;
            return;
        }

        var relativePath = skin == AppSkin.WinXP ? "Themes/WinXP.axaml" : "Themes/Win7.axaml";

        for (var index = application.Styles.Count - 1; index >= 0; index--)
        {
            if (application.Styles[index] is StyleInclude include &&
                include.Source is { } source &&
                SkinRelativePaths.Any(path => source.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase)))
            {
                application.Styles.RemoveAt(index);
            }
        }
        var styleInclude = new StyleInclude(new Uri(AssemblyRoot, UriKind.Absolute))
        {
            Source = new Uri(AssemblyRoot + relativePath, UriKind.Absolute)
        };
        application.Styles.Add(styleInclude);
        Current = skin;
    }
}
