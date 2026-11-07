namespace FileShare.Desktop.Services;

/// <summary>
/// 桌面端 UI 偏好（皮肤 / 语言 / 托盘行为），单独持久化到 <c>desktop-ui.json</c>，
/// 与 Core 的 <c>settings.json</c> 分离。
/// <para>语言存 BCP-47 字符串而不是枚举，新增语言不需要改本 DTO。</para>
/// </summary>
public sealed class DesktopUiPreferences
{
    /// <summary>选中皮肤。</summary>
    public AppSkin Skin { get; set; } = AppSkin.Win7;

    /// <summary>界面语言（BCP-47，例如 zh-CN / en-US）。</summary>
    public string LanguageCode { get; set; } = "zh-CN";

    /// <summary>最小化窗口时隐藏到系统托盘。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>关闭主窗口时隐藏到托盘（而不是退出）。</summary>
    public bool CloseToTray { get; set; }
}
