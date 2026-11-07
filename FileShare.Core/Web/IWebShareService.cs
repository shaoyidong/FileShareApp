namespace FileShare.Core.Web;

/// <summary>
/// 网页分享配置。
/// </summary>
public sealed class WebShareOptions
{
    /// <summary>监听端口。</summary>
    public int Port { get; init; } = 8088;

    /// <summary>访问 PIN（为空时不校验，不建议）。</summary>
    public string Pin { get; init; } = string.Empty;

    /// <summary>接收文件保存目录。</summary>
    public required string SaveDirectory { get; init; }

    /// <summary>对外展示的地址（可选；不填则自动选取本机 IPv4）。</summary>
    public string? PublicAddress { get; init; }
}

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

    /// <summary>收到文件时触发（绝对路径）。</summary>
    event Action<string>? FileReceived;

    /// <summary>启动服务（重复调用会先停止旧实例）。</summary>
    void Start(WebShareOptions options);

    /// <summary>停止服务。</summary>
    void Stop();
}
