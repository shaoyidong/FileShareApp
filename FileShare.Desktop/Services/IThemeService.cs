using System.Collections.Generic;
using FileShare.Desktop.Theming;

namespace FileShare.Desktop.Services;

/// <summary>
/// 主题管理：主题由「数据目录 / themes / *.json」配色包 + 编译期 axaml 基座组合而成，
/// 可在运行时切换 / 重载（AOT 安全，不再经 XAML URI 加载）。
/// </summary>
public interface IThemeService
{
    /// <summary>当前主题 id（对应主题包的 id / 文件名）。</summary>
    string Current { get; }

    /// <summary>所有可用主题（按注册顺序，用于生成设置页下拉框）。</summary>
    IReadOnlyList<ThemePack> AvailableThemes { get; }

    /// <summary>
    /// 在 <c>App.Initialize</c> 中、窗口创建之前调用：摘除静态声明残留的皮肤实例，
    /// 随后 <see cref="Apply"/> 会添加唯一一套生效皮肤。
    /// </summary>
    void InitializeFromApplication(Avalonia.Application application);

    /// <summary>应用指定主题；id 未注册时回退到内置 win7。</summary>
    void Apply(string themeId);

    /// <summary>重新扫描主题包文件并立即重新应用当前主题；返回成功加载的文件数。</summary>
    int ReloadThemes();
}
