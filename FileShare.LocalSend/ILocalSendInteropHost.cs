namespace FileShare.LocalSend;

/// <summary>
/// LocalSend 协议互通（接收方向）：
/// <list type="bullet">
/// <item>用 <c>LocalSendServer</c> 在本机启动 LocalSend v2 HTTP 服务（默认 53317），
/// 官方 LocalSend 客户端可以把文件直接发到本机；</item>
/// <item>用 <c>MulticastDiscoveryService</c> 发送组播宣告，让官方客户端能在设备列表里发现本机
/// （否则对方根本不知道我们存在）。</item>
/// </list>
/// <para>注意：官方客户端的 HTTPS/mTLS 指纹校验、PIN 交互与发送方向尚未完整接入，
/// 见 docs/LocalSend-功能对照与差距分析.md。</para>
/// </summary>
public interface ILocalSendInteropHost : IDisposable
{
    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>本机 LocalSend 端点（例如 http://192.168.1.10:53317）。</summary>
    string? Endpoint { get; }

    /// <summary>最近一次启动失败的原因（成功时为 null）。</summary>
    string? LastError { get; }

    /// <summary>按当前设置启动或停止。</summary>
    void Apply();

    /// <summary>收到文件时触发（已落盘的绝对路径）。</summary>
    event Action<string>? FileReceived;

    /// <summary>通过 LocalSend 组播发现到新设备时触发。</summary>
    event Action<LocalSendPeer>? PeerDiscovered;

    /// <summary>已发现的 LocalSend 设备。</summary>
    IReadOnlyCollection<LocalSendPeer> Peers { get; }

    /// <summary>
    /// 用 IP + 端口探测对端是否是 LocalSend 设备（跨子网 / 组播被拦时的兜底）。
    /// </summary>
    Task<LocalSendPeer?> ProbePeerAsync(string ipAddress, int port, CancellationToken cancellationToken = default);

    /// <summary>发送单个文件到 LocalSend 设备。</summary>
    Task<LocalSendSendResult> SendFileAsync(
        LocalSendPeer peer,
        string filePath,
        string? relativePath = null,
        Action<ulong, ulong>? progress = null,
        CancellationToken cancellationToken = default);
}
