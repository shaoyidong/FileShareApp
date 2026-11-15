using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using FileShare.Desktop.Themes;
using FileShare.Desktop.Theming;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FileShare.Desktop.Services;

/// <inheritdoc cref="IThemeService"/>
/// <remarks>
/// AOT 修复要点：三套皮肤 axaml 都带 <c>x:Class</c>，编译期会生成
/// <see cref="Win7Skin"/> / <see cref="WinXPSkin"/> / <see cref="WinXPOliveSkin"/> 类，
/// 这里直接 <c>new</c> 这些编译产物，完全不走 <c>StyleInclude</c> 的运行时 XAML URI 加载
/// （该路径在 NativeAOT / TrimMode=full 下会报 "No precompiled XAML found"）。
/// <para>
/// 配色由 JSON 主题包覆盖：把值同时写进皮肤自身的 Resources 与 Application.Resources，
/// 保证无论 DynamicResource 从哪一层解析都能拿到新值；未覆盖的键沿用 axaml 基座默认值。
/// </para>
/// </remarks>
public sealed class ThemeService : IThemeService
{
    private readonly IUiPreferencesService _uiPreferences;
    private bool _initialized;

    public ThemeService(IUiPreferencesService uiPreferences)
    {
        _uiPreferences = uiPreferences;
        Current = NormalizeId(uiPreferences.Current.SkinId);
    }

    /// <inheritdoc />
    public string Current { get; private set; } = ThemePack.DefaultId;

    /// <inheritdoc />
    public IReadOnlyList<ThemePack> AvailableThemes => ThemePackLoader.Packs;
    
    public void InitializeFromApplication(Application application)
    {
        if (application is null)
        {
            return;
        }

        RemoveAllSkins(application);
        _initialized = true;
    }

    /// <inheritdoc />
    public void Apply(string themeId)
    {
        var id = ResolveId(themeId);
        var pack = ThemePackLoader.Find(id);

        var application = Application.Current;
        if (application is null || !_initialized)
        {
            // 设计器 / 单元测试环境：只记录状态，不做实际切换
            Current = id;
            _uiPreferences.SetSkinId(id);
            return;
        }

        RemoveAllSkins(application);

        var skin = CreateSkin(pack?.Base ?? ThemePack.DefaultBase);
        if (pack is not null)
        {
            ApplyColors(application, skin, pack);
        }

        application.Styles.Add(skin);

        Current = id;

        // 主题属于桌面端私有配置，单独持久化（不写进 Core 的 settings.json）
        _uiPreferences.SetSkinId(id);
    }

    /// <inheritdoc />
    public int ReloadThemes()
    {
        var loaded = ThemePackLoader.Reload();

        // 当前主题可能被改过颜色或被删除，重新应用一次（ResolveId 会处理失效 id）
        Apply(Current);
        return loaded;
    }

    /// <summary>按基座 id 构造编译期皮肤实例（AOT 安全）。</summary>
    private static Styles CreateSkin(string baseSkin) => baseSkin switch
    {
        "winxp" => new WinXPSkin(),
        "winxp-olive" => new WinXPOliveSkin(),
        _ => new Win7Skin()
    };

    /// <summary>
    /// 把基座默认值 + JSON 覆盖值写入皮肤 Resources 与 Application.Resources。
    /// 两边都写是为了兼容 Avalonia 不同层级的 DynamicResource 查找顺序。
    /// </summary>
    private static void ApplyColors(Application application, Styles skin, ThemePack pack)
    {
        IResourceDictionary? appResources = null;
        try
        {
            appResources = application.Resources;
        }
        catch (Exception)
        {
            // 无 Application.Resources 的环境只写皮肤自身即可
        }

        void Write(string key, object? value)
        {
            if (value is null)
            {
                return;
            }

            try { skin.Resources[key] = value; } catch (Exception) { }
            try { appResources?[key] = value; } catch (Exception) { }
        }

        foreach (var key in ThemePack.ResourceKeys)
        {
            object? baseValue = null;
            try { baseValue = skin.Resources[key]; } catch (Exception) { }

            var value = pack.Colors.TryGetValue(key, out var overridden) && overridden is not null
                ? overridden
                : baseValue;
            Write(key, value);
        }

        // 允许主题包引入额外的 Fs* 自定义资源键
        foreach (var (key, value) in pack.Colors)
        {
            if (!ThemePack.ResourceKeys.Contains(key))
            {
                Write(key, value);
            }
        }
    }

    private static string ResolveId(string? themeId)
    {
        if (!string.IsNullOrWhiteSpace(themeId) && ThemePackLoader.Find(themeId) is not null)
        {
            return themeId!;
        }

        if (ThemePackLoader.Find(ThemePack.DefaultId) is not null)
        {
            return ThemePack.DefaultId;
        }

        // 内置主题全部缺失时不改写用户偏好，仍以默认基座渲染
        return string.IsNullOrWhiteSpace(themeId) ? ThemePack.DefaultId : themeId!;
    }

    private static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? ThemePack.DefaultId : id!;

    /// <summary>
    /// 移除所有皮肤样式（含旧版 App.axaml 静态 StyleInclude 展开出来的纯 Styles 实例），
    /// 保留 SimpleTheme 基础主题。
    /// </summary>
    private static void RemoveAllSkins(Application application)
    {
        for (var i = application.Styles.Count - 1; i >= 0; i--)
        {
            var style = application.Styles[i];
            if (style is Win7Skin or WinXPSkin or WinXPOliveSkin ||
                style.GetType() == typeof(Styles))
            {
                application.Styles.RemoveAt(i);
            }
        }
    }
}
