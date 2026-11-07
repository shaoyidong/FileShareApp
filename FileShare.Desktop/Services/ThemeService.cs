using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Services;

/// <inheritdoc cref="IThemeService"/>
/// <remarks>
/// AOT 安全实现：三套皮肤的 <see cref="StyleInclude"/> 实例全部由 App.axaml 在编译期静态创建
/// （XAML 编译器强类型加载，不会被 TrimMode=full 裁掉）。
/// 切换时只把缓存的实例从 <c>Application.Styles</c> 移除再回填，
/// 不再在运行时 <c>new StyleInclude(new Uri(...))</c>。
/// </remarks>
public sealed class ThemeService : IThemeService
{
    /// <summary>皮肤枚举 → 样式文件（用于按 Source 识别静态声明的实例）。</summary>
    private static readonly (AppSkin Skin, string RelativePath)[] SkinFiles =
    {
        (AppSkin.Win7, "Themes/Win7.axaml"),
        (AppSkin.WinXP, "Themes/WinXP.axaml"),
        (AppSkin.WinXPOlive, "Themes/WinXPOlive.axaml"),
    };

    private readonly IUiPreferencesService _uiPreferences;
    private readonly Dictionary<AppSkin, StyleInclude> _skinStyles = new();

    public ThemeService(IUiPreferencesService uiPreferences)
    {
        _uiPreferences = uiPreferences;
        Current = uiPreferences.Current.Skin;
    }

    /// <inheritdoc />
    public AppSkin Current { get; private set; } = AppSkin.Win7;

    /// <inheritdoc />
    public IReadOnlyList<AppSkin> AvailableSkins { get; } = new[] { AppSkin.Win7, AppSkin.WinXP, AppSkin.WinXPOlive };

    /// <summary>
    /// 在 <c>App.Initialize</c> 中、窗口创建之前调用：缓存 App.axaml 静态声明的皮肤实例，
    /// 并摘除除默认 Win7 之外的皮肤，保证首帧只有一套皮肤生效（无叠加、无闪烁）。
    /// </summary>
    public void InitializeFromApplication(Application application)
    {
        if (application is null)
        {
            return;
        }

        _skinStyles.Clear();
        foreach (var style in application.Styles)
        {
            if (style is not StyleInclude include || include.Source is not { } source)
            {
                continue;
            }

            foreach (var (skin, relativePath) in SkinFiles)
            {
                if (source.AbsolutePath.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    _skinStyles[skin] = include;
                    break;
                }
            }
        }

        // 静态默认只保留 Win7；随后 Apply(Current) 会按用户偏好回填目标实例。
        foreach (var (skin, _) in SkinFiles)
        {
            if (skin != AppSkin.Win7 && _skinStyles.TryGetValue(skin, out var extra))
            {
                application.Styles.Remove(extra);
            }
        }
    }

    /// <inheritdoc />
    public void Apply(AppSkin skin)
    {
        if (!Enum.IsDefined(skin))
        {
            skin = AppSkin.Win7;
        }

        var application = Application.Current;
        if (application is null || _skinStyles.Count == 0)
        {
            // 设计器 / 单元测试环境：只记录状态，不做实际切换
            Current = skin;
            _uiPreferences.SetSkin(skin);
            return;
        }

        // 移除全部皮肤实例，再回填目标实例；不使用运行时 URI 加载。
        foreach (var include in _skinStyles.Values)
        {
            application.Styles.Remove(include);
        }

        if (!_skinStyles.TryGetValue(skin, out var target))
        {
            skin = AppSkin.Win7;
            _skinStyles.TryGetValue(skin, out target);
        }

        if (target is not null)
        {
            application.Styles.Add(target);
        }

        Current = skin;

        // 皮肤属于桌面端私有配置，单独持久化（不写进 Core 的 settings.json）
        _uiPreferences.SetSkin(skin);
    }
}
