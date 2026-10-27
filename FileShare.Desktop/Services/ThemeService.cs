using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using FileShare.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FileShare.Desktop.Services;

/// <summary>
/// 皮肤管理：运行时在 Windows 7 / Windows XP（蓝）/ Windows XP（橄榄绿）之间切换。
/// </summary>
public interface IThemeService
{
    /// <summary>当前皮肤。</summary>
    AppSkin Current { get; }

    /// <summary>所有可用皮肤（按展示顺序）。</summary>
    IReadOnlyList<AppSkin> AvailableSkins { get; }

    /// <summary>应用皮肤（会替换 <see cref="Application.Styles"/> 中的皮肤 StyleInclude）。</summary>
    void Apply(AppSkin skin);
}

/// <inheritdoc cref="IThemeService"/>
public sealed class ThemeService : IThemeService
{
    private const string AssemblyRoot = "avares://FileShare.Desktop/";

    /// <summary>皮肤枚举 → 样式文件。新增皮肤只需在这里加一行 + 新增 axaml。</summary>
    private static readonly Dictionary<AppSkin, string> SkinFiles = new()
    {
        [AppSkin.Win7] = "Themes/Win7.axaml",
        [AppSkin.WinXP] = "Themes/WinXP.axaml",
        [AppSkin.WinXPOlive] = "Themes/WinXPOlive.axaml",
    };

    private readonly IUiPreferencesService _uiPreferences;

    public ThemeService(IUiPreferencesService uiPreferences)
    {
        _uiPreferences = uiPreferences;
        Current = uiPreferences.Current.Skin;
    }

    /// <inheritdoc />
    public AppSkin Current { get; private set; } = AppSkin.Win7;

    /// <inheritdoc />
    public IReadOnlyList<AppSkin> AvailableSkins { get; } = new[] { AppSkin.Win7, AppSkin.WinXP, AppSkin.WinXPOlive };

    /// <inheritdoc />
    public void Apply(AppSkin skin)
    {
        if (!SkinFiles.TryGetValue(skin, out var relativePath))
        {
            relativePath = SkinFiles[AppSkin.Win7];
            skin = AppSkin.Win7;
        }

        var application = Application.Current;
        if (application is null)
        {
            // 设计器 / 单元测试环境：只记录状态，不做实际切换
            Current = skin;
            _uiPreferences.SetSkin(skin);
            return;
        }

        for (var index = application.Styles.Count - 1; index >= 0; index--)
        {
            if (application.Styles[index] is StyleInclude include &&
                include.Source is { } source &&
                SkinFiles.Values.Any(path => source.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase)))
            {
                application.Styles.RemoveAt(index);
            }
        }

        application.Styles.Add(new StyleInclude(new Uri(AssemblyRoot, UriKind.Absolute))
        {
            Source = new Uri(AssemblyRoot + relativePath, UriKind.Absolute)
        });

        Current = skin;

        // 皮肤属于桌面端私有配置，单独持久化（不写进 Core 的 settings.json）
        _uiPreferences.SetSkin(skin);
    }
}
