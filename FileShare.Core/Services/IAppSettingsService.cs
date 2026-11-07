namespace FileShare.Core.Services;

/// <summary>
/// 设置服务：进程内单例，负责读取/保存 <see cref="AppSettings"/>。
/// </summary>
public interface IAppSettingsService
{
    /// <summary>当前设置（只读使用；修改请调用 <see cref="Update"/>）。</summary>
    AppSettings Current { get; }

    /// <summary>设置文件路径。</summary>
    string SettingsFilePath { get; }

    /// <summary>设置变化事件（同一进程内）。</summary>
    event EventHandler? Changed;

    /// <summary>修改设置并立即持久化。</summary>
    void Update(Action<AppSettings> mutate);

    /// <summary>把当前设置写入磁盘。</summary>
    void Save();

    /// <summary>从磁盘重新加载。</summary>
    /// <returns>文件存在且解析成功时返回 true。</returns>
    bool Reload();
}
