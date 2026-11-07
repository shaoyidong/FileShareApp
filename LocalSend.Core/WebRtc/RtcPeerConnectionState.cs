namespace LocalSend.Core.WebRtc;

/// <summary>
/// Peer connection 状态，对应 Rust 端 <c>RTCPeerConnectionState</c>。
/// </summary>
public enum RtcPeerConnectionState
{
    Unspecified,
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}
