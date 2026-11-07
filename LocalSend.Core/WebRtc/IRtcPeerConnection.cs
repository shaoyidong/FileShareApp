namespace LocalSend.Core.WebRtc;

/// <summary>
/// WebRTC peer connection 抽象，对应 Rust 端
/// <c>webrtc::peer_connection::RTCPeerConnection</c>。
/// </summary>
/// <remarks>
/// LocalSend 在数据通道里传输文件，因此本接口只暴露 LocalSend 真正用到的功能：
/// 创建 offer/answer、设置本地 / 远端 SDP、创建数据通道、监听对端创建的数据通道、
/// 监听连接状态变化。
/// <para>
/// 相关类型已各自拆到独立文件：IRtcDataChannel.cs / RtcDataChannelMessage.cs /
/// RtcPeerConnectionState.cs / IRtcPeerConnectionFactory.cs。
/// </para>
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
    /// 当状态变为 <c>Disconnected</c> 时应触发 <paramref name="callback"/> 对应的状态。
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
