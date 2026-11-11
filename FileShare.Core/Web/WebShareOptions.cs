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

    /// <summary>
    /// 上传前接收审批回调：(文件名, 字节数) → 传输列表 ID；返回 null 表示拒绝接收。
    /// <para>由宿主实现，用于「非收藏设备需确认」，避免网页上传静默落盘。</para>
    /// </summary>
    public Func<string, long, Task<string?>>? ReceiveApprovalAsync { get; init; }
}

/// <summary>
/// 极简网页分享：在局域网内起一个 http 服务，其它设备用浏览器打开链接、输入 PIN 即可上传文件。
/// <para>不依赖管理员权限（不用 HttpListener 的 URL ACL），直接基于 TcpListener 实现最小 HTTP/1.1 子集。</para>
/// <para>只有两个端点：<c>GET /</c> 页面、<c>POST /upload?name=xxx</c> 上传（body 为原始文件字节，避免 multipart 解析）。</para>
/// </summary>
