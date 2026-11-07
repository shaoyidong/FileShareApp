namespace FileShare.Desktop.Services;

/// <summary>
/// 桌面端皮肤。**只属于 Desktop**：移动端有自己的视觉体系，不应该被 Win7/XP 皮肤污染，
/// 因此皮肤选择不放在 Core 的 AppSettings 里，而是由桌面项目单独持久化。
/// </summary>
public enum AppSkin
{
    /// <summary>Windows 7 / Aero（默认）。</summary>
    Win7 = 0,

    /// <summary>Windows XP / Luna Blue（经典蓝）。</summary>
    WinXP = 1,

    /// <summary>Windows XP / Luna Olive（橄榄绿）。</summary>
    WinXPOlive = 2,
}
