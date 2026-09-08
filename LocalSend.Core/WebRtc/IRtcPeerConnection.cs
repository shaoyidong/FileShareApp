using System.Threading.Channels;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// WebRTC 数据通道抽象，对应 Rust 端使用的
/// <c>webrtc::data_channel::RTCDataChannel</c>。
/// </summary>
/// <remarks>
/// .NET 没有内置 WebRTC 库，因此本接口把 Rust 版对
/// <c>RTCDataChannel</c> 的依赖抽象出来。
/// 实际项目可以用任意 WebRTC 库（例如 SIPSorcery、aiortc 等）
/// 实现 <see cref="IRtcPeerConnection"/> 与 <see cref="IRtcDataChannel"/>。
/// </remarks>
public interface IRtcDataChannel : IAsyncDisposable
{
    /// <summary>数据通道标签，Rust 端固定为 <c>"data"</c>。</summary>
    string Label { get; }

    /// <summary>
    /// 数据通道是否已打开。对应 Rust 端 <c>on_open</c> 回调。
    /// </summary>
    Task Opened { get; }

    /// <summary>
    /// 接收消息的流：每收到一条消息（文本 / 二进制）都会写到这里。
    /// 对应 Rust 端 <c>to_receive_stream</c> 把 <c>on_message</c> 转成 mpsc 流。
    /// </summary>
    ChannelReader<RtcDataChannelMessage> Messages { get; }

    /// <summary>发送一条文本消息，对应 <c>data_channel.send_text(...)</c>。</summary>
    Task SendTextAsync(string text, CancellationToken ct = default);

    /// <summary>发送一条二进制消息，对应 <c>data_channel.send(...)</c>。</summary>
    Task SendBinaryAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// 关闭数据通道，对应 <c>data_channel.close()</c>。
    /// </summary>
    Task CloseAsync(CancellationToken ct = default);

    /// <summary>
    /// 等待发送缓冲区清空，对应 Rust 端 <c>wait_buffer_empty</c>。
    /// 实现可以选择轮询 <c>BufferedAmount</c> 或者直接返回。
    /// </summary>
    Task WaitBufferEmptyAsync(CancellationToken ct = default);
}

/// <summary>
/// 一条来自对端的数据通道消息，对应 Rust 端 <c>DataChannelMessage</c>。
/// </summary>
public readonly record struct RtcDataChannelMessage(bool IsString, byte[] Data)
{
    /// <summary>若是文本消息，按 UTF-8 解码后的字符串。</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Data);
}

/// <summary>
/// WebRTC peer connection 抽象，对应 Rust 端
/// <c>webrtc::peer_connection::RTCPeerConnection</c>。
/// </summary>
/// <remarks>
/// LocalSend 在数据通道里传输文件，因此本接口只暴露 LocalSend 真正用到的功能：
/// 创建 offer/answer、设置本地 / 远端 SDP、创建数据通道、监听对端创建的数据通道、
/// 监听连接状态变化。
/// </remarks>
public interface IRtcPeerConnection : IAsyncDisposable
{
    /// <summary>
    /// 由本端创建数据通道。对应 Rust 端 <c>peer_connection.create_data_channel(...)</c>。
    /// </summary>
    /// <param name="label">通道标签，LocalSend 固定为 <c>"data"</c>。</param>
    IRtcDataChannel CreateDataChannel(string label);

    /// <summary>
    /// 注册回调：对端创建了数据通道时触发。对应 Rust 端 <c>on_data_channel</c>。
    /// </summary>
    /// <param name="callback">收到对端数据通道时调用，参数是对端的 <see cref="IRtcDataChannel"/>。</param>
    void OnDataChannel(Action<IRtcDataChannel> callback);

    /// <summary>
    /// 注册回调：peer connection 状态变化时触发。
    /// 当状态变为 <c>Disconnected</c> 时应触发 <paramref name="onDisconnected"/>。
    /// 对应 Rust 端 <c>on_peer_connection_state_change</c>。
    /// </summary>
    void OnPeerConnectionStateChange(Action<RtcPeerConnectionState> callback);

    /// <summary>创建 SDP offer，对应 <c>create_offer</c>。</summary>
    Task<string> CreateOfferAsync(CancellationToken ct = default);

    /// <summary>创建 SDP answer，对应 <c>create_answer</c>。</summary>
    Task<string> CreateAnswerAsync(CancellationToken ct = default);

    /// <summary>
    /// 设置本地 SDP，对应 <c>set_local_description</c>。
    /// 实现内部需要等待 ICE 候选收集完成（对应 Rust 端 <c>gathering_complete_promise</c>）。
    /// </summary>
    Task SetLocalDescriptionAsync(string sdp, CancellationToken ct = default);

    /// <summary>设置远端 SDP，对应 <c>set_remote_description</c>。</summary>
    Task SetRemoteDescriptionAsync(string sdp, bool isOffer, CancellationToken ct = default);

    /// <summary>关闭 peer connection，对应 <c>peer_connection.close()</c>。</summary>
    Task CloseAsync(CancellationToken ct = default);
}

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

/// <summary>
/// 工厂接口：创建 <see cref="IRtcPeerConnection"/>。
/// 对应 Rust 端 <c>create_peer_connection</c>，但拆为工厂便于依赖注入。
/// </summary>
public interface IRtcPeerConnectionFactory
{
    /// <param name="stunServers">STUN 服务器 URL 列表（如 <c>stun:stun.l.google.com:19302</c>）。</param>
    IRtcPeerConnection Create(IReadOnlyList<string> stunServers);
}
