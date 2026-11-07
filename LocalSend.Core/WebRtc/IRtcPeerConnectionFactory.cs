namespace LocalSend.Core.WebRtc;

/// <summary>
/// 工厂接口：创建 <see cref="IRtcPeerConnection"/>。
/// 对应 Rust 端 <c>create_peer_connection</c>，但拆为工厂便于依赖注入。
/// </summary>
public interface IRtcPeerConnectionFactory
{
    /// <param name="stunServers">STUN 服务器 URL 列表（如 <c>stun:stun.l.google.com:19302</c>）。</param>
    IRtcPeerConnection Create(IReadOnlyList<string> stunServers);
}
