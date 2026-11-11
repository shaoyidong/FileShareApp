namespace FileShare.Desktop.Services;

/// <summary>
/// 网页分享宿主：根据设置启动/停止 <see cref="WebShareService"/>，并负责 PIN 的生成与持久化。
/// </summary>
public interface IWebShareHost
{
    /// <summary>按当前设置应用（启动或停止）。</summary>
    void Apply();

    /// <summary>当前访问地址（未启用时为 null）。</summary>
    string? Url { get; }

    /// <summary>当前 PIN。</summary>
    string Pin { get; }

    /// <summary>重新生成 PIN 并重启服务。</summary>
    string RegeneratePin();
}
