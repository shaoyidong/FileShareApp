namespace LocalSend.Core.WebRtc.Signaling;

/// <summary>
/// 高级信令连接，对应 Rust 端
/// <c>src/webrtc/signaling.rs</c> 中的 <c>ManagedSignalingConnection</c>。
/// </summary>
/// <remarks>
/// 与 <see cref="SignalingConnection"/> 的区别：
/// <list type="bullet">
///   <item>持有了一个 <c>session_id -> 回调</c> 的字典，当收到匹配的
///         <see cref="WsServerMessage.Answer"/> 时调用对应回调，从而让
///         <c>send_offer</c> 的发起者以"一次性回调"的方式拿到对方的 answer。</item>
///   <item>不再持有 <see cref="WsServerMessage"/> 接收通道——所有消息已被
///         <see cref="SignalingConnection.StartListener"/> 转发到外部监听器。</item>
/// </list>
/// 该类只暴露发送 / 注册回调的方法；具体的 WebSocket 收发由
/// <see cref="SignalingConnection"/> 完成。
/// </remarks>
public sealed class ManagedSignalingConnection
{
    /// <summary>由服务器在 Hello 消息里分配的本端客户端信息。</summary>
    public WsClientInfo Client { get; }

    /// <summary>发送 Update 消息的委托，由 <see cref="SignalingConnection"/> 注入。</summary>
    private readonly Func<WsClientInfoWithoutId, CancellationToken, Task> _sendUpdate;

    /// <summary>发送 SDP Offer 的委托，由 <see cref="SignalingConnection"/> 注入。</summary>
    private readonly Func<string, Guid, string, CancellationToken, Task> _sendOffer;

    /// <summary>发送 SDP Answer 的委托，由 <see cref="SignalingConnection"/> 注入。</summary>
    private readonly Func<string, Guid, string, CancellationToken, Task> _sendAnswer;

    /// <summary>注册 answer 回调的委托，由 <see cref="SignalingConnection"/> 注入。</summary>
    private readonly Func<string, Func<WsServerSdpMessage, Task>, Task> _onAnswer;

    internal ManagedSignalingConnection(
        WsClientInfo client,
        Func<WsClientInfoWithoutId, CancellationToken, Task> sendUpdate,
        Func<string, Guid, string, CancellationToken, Task> sendOffer,
        Func<string, Guid, string, CancellationToken, Task> sendAnswer,
        Func<string, Func<WsServerSdpMessage, Task>, Task> onAnswer)
    {
        Client = client;
        _sendUpdate = sendUpdate;
        _sendOffer = sendOffer;
        _sendAnswer = sendAnswer;
        _onAnswer = onAnswer;
    }

    /// <summary>更新本端信息。对应 Rust 端 <c>ManagedSignalingConnection::send_update</c>。</summary>
    public Task SendUpdateAsync(WsClientInfoWithoutId info, CancellationToken ct = default)
        => _sendUpdate(info, ct);

    /// <summary>发送 SDP Offer。对应 Rust 端 <c>send_offer</c>。</summary>
    public Task SendOfferAsync(string sessionId, Guid target, string sdp, CancellationToken ct = default)
        => _sendOffer(sessionId, target, sdp, ct);

    /// <summary>发送 SDP Answer。对应 Rust 端 <c>send_answer</c>。</summary>
    public Task SendAnswerAsync(string sessionId, Guid target, string sdp, CancellationToken ct = default)
        => _sendAnswer(sessionId, target, sdp, ct);

    /// <summary>
    /// 注册一个一次性回调：当收到 <see cref="WsServerMessage.Answer"/> 且其
    /// <see cref="WsServerSdpMessage.SessionId"/> 等于 <paramref name="sessionId"/> 时触发。
    /// 对应 Rust 端 <c>ManagedSignalingConnection::on_answer</c>。
    /// </summary>
    public Task OnAnswerAsync(string sessionId, Func<WsServerSdpMessage, Task> callback)
        => _onAnswer(sessionId, callback);
}
