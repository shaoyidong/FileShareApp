namespace FileShare.Core.Web;

/// <summary>
/// 极简网页分享：在局域网内起一个 http 服务，其它设备用浏览器打开链接、输入 PIN 即可上传文件。
/// <para>不依赖管理员权限（不用 HttpListener 的 URL ACL），直接基于 TcpListener 实现最小 HTTP/1.1 子集。</para>
/// <para>只有两个端点：<c>GET /</c> 页面、<c>POST /upload?name=xxx</c> 上传（body 为原始文件字节，避免 multipart 解析）。</para>
/// </summary>
public interface IWebShareService : IDisposable
{
    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>浏览器访问地址（未运行时为 null）。</summary>
    string? Url { get; }

    /// <summary>当前 PIN。</summary>
    string Pin { get; }

    /// <summary>收到文件时触发（已落盘的绝对路径、传输列表 ID）。</summary>
    event Action<string, string?>? FileReceived;

    /// <summary>文件保存失败时触发（路径、传输列表 ID）。</summary>
    event Action<string, string?>? FileReceiveFailed;

    /// <summary>启动服务（重复调用会先停止旧实例）。</summary>
    void Start(WebShareOptions options);

    /// <summary>停止服务。</summary>
    void Stop();
}
