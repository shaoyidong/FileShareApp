namespace FileShare.Desktop.Models;

/// <summary>
/// 桌面端 UI 偏好（主题 / 语言 / 托盘行为），单独持久化到 <c>desktop-ui.json</c>，
/// 与 Core 的 <c>settings.json</c> 分离。
/// <para>主题与语言都存字符串 id 而不是枚举，新增 JSON 主题包 / 语言包不需要改本 DTO。</para>
/// </summary>
public sealed class DesktopUiPreferences
{
    /// <summary>选中主题 id（对应数据目录 themes/*.json 里的 id）。</summary>
    public string SkinId { get; set; } = "win7";

    /// <summary>界面语言（BCP-47，例如 zh-CN / en-US）。</summary>
    public string LanguageCode { get; set; } = "zh-CN";

    /// <summary>最小化窗口时隐藏到系统托盘。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>关闭主窗口时隐藏到托盘（而不是退出）。</summary>
    public bool CloseToTray { get; set; }
}
