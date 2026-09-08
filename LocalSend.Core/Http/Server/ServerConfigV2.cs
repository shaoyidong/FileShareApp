using System.Threading.Channels;
using LocalSend.Core.Http.Server.V2;

namespace LocalSend.Core.Http.Server;

/// <summary>
/// v2 协议端点配置，对应 Rust 端 <c>src/http/server/mod.rs</c> 中的 <c>ServerConfigV2</c>。
/// </summary>
public sealed class ServerConfigV2
{
    /// <summary>可选 PIN。</summary>
    public string? Pin { get; set; }

    /// <summary>服务器事件输出通道。</summary>
    public Channel<ServerEventV2> EventTx { get; set; } =
        Channel.CreateUnbounded<ServerEventV2>();
}

/// <summary>
/// TLS 配置，对应 Rust 端 <c>TlsConfig</c>。
/// </summary>
public sealed class TlsConfig
{
    public string Cert { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
}
