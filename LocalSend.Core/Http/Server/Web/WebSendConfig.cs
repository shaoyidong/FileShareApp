using System.Net;
using System.Threading.Channels;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Model;
using LocalSend.Core.Util;

namespace LocalSend.Core.Http.Server.Web;

/// <summary>
/// Web 发送（Download API）配置，对应 Rust 端 <c>WebSendConfig</c>。
/// </summary>
public sealed class WebSendConfig
{
    /// <summary>共享文件的元数据，键为 file ID。</summary>
    public Dictionary<string, FileDto> Files { get; set; } = new();

    /// <summary>可选 PIN。</summary>
    public string? Pin { get; set; }

    /// <summary>Web 页面文案。</summary>
    public WebSendI18n I18n { get; set; } = new();

    /// <summary>服务器事件输出通道。</summary>
    public Channel<WebSendEvent> EventTx { get; set; } =
        Channel.CreateUnbounded<WebSendEvent>();
}

/// <summary>
/// Web 发送的运行时状态，对应 Rust 端 <c>WebPageState</c>。
/// </summary>
public sealed class WebPageState
{
    public Dictionary<string, FileDto> Files { get; }
    public string? Pin { get; }
    public WebSendI18n I18n { get; }
    public Channel<WebSendEvent> EventTx { get; }

    /// <summary>下载会话，键为 session ID（客户端 IP）。</summary>
    public SemaphoreSlim SessionsLock { get; } = new(1, 1);
    public Dictionary<string, WebSendSession> Sessions { get; } = new();

    public SemaphoreSlim PinAttemptsLock { get; } = new(1, 1);
    public LruCache<IPAddress, uint> PinAttempts { get; } = new(200);

    public WebPageState(WebSendConfig config)
    {
        Files = config.Files;
        Pin = config.Pin;
        I18n = config.I18n;
        EventTx = config.EventTx;
    }
}

/// <summary>Web 客户端的下载会话，对应 Rust 端 <c>WebSendSession</c>。</summary>
public sealed class WebSendSession
{
    public IPAddress Ip { get; init; } = IPAddress.None;
    /// <summary>prepare-download 等待应用决策时为 false；接受后置 true。</summary>
    public bool Accepted { get; set; }
}
