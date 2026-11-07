namespace FileShare.Desktop.Services;

/// <summary>
/// 桌面端 UI 偏好存取。
/// </summary>
public interface IUiPreferencesService
{
    /// <summary>当前偏好。</summary>
    DesktopUiPreferences Current { get; }

    /// <summary>更新皮肤并立即保存。</summary>
    void SetSkin(AppSkin skin);

    /// <summary>按 BCP-47 code 更新语言并立即保存。</summary>
    void SetLanguage(string languageCode);

    /// <summary>更新托盘偏好并立即保存。</summary>
    void SetTrayPreferences(bool minimizeToTray, bool closeToTray);
}
