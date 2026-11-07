using System.Collections.Generic;

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

    /// <summary>应用皮肤（替换 <c>Application.Styles</c> 中的皮肤 StyleInclude）。</summary>
    void Apply(AppSkin skin);
}
