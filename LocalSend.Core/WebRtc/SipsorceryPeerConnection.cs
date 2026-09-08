#if SIPSORCERY
using SIPSorcery.Net;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// 基于 SIPSorcery 的 <see cref="IRtcPeerConnection"/> 实现。
/// 封装 SIPSorcery.Net.<see cref="RTCPeerConnection"/>。
/// </summary>
/// <remarks>
/// 需要在 csproj 中添加 SIPSorcery 包引用并定义 SIPSORCERY 编译符号才能使用。
/// </remarks>
public sealed class SipsorceryPeerConnection : IRtcPeerConnection
{
    private readonly RTCPeerConnection _inner;
    private Action<IRtcDataChannel>? _onDataChannelCallback;
    private Action<RtcPeerConnectionState>? _onStateChangeCallback;

    /// <summary>
    /// 构造并绑定 SIPSorcery PeerConnection 事件。
    /// </summary>
    internal SipsorceryPeerConnection(RTCPeerConnection inner)
    {
        _inner = inner;

        // 对端创建数据通道时触发
        inner.onDataChannel += dc =>
        {
            var wrapped = new SipsorceryDataChannel(dc);
            _onDataChannelCallback?.Invoke(wrapped);
        };

        // Peer connection 状态变化
        inner.onconnectionstatechange += state =>
        {
            var mapped = MapState(state);
            _onStateChangeCallback?.Invoke(mapped);
        };
    }

    /// <inheritdoc />
    public IRtcDataChannel CreateDataChannel(string label)
    {
        var dc = _inner.createDataChannel(label);
        return new SipsorceryDataChannel(dc);
    }

    /// <inheritdoc />
    public void OnDataChannel(Action<IRtcDataChannel> callback)
    {
        _onDataChannelCallback = callback;
    }

    /// <inheritdoc />
    public void OnPeerConnectionStateChange(Action<RtcPeerConnectionState> callback)
    {
        _onStateChangeCallback = callback;
    }

    /// <inheritdoc />
    public Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        var sdp = _inner.createOffer();
        return Task.FromResult(sdp.ToString());
    }

    /// <inheritdoc />
    public Task<string> CreateAnswerAsync(CancellationToken ct = default)
    {
        var sdp = _inner.createAnswer();
        return Task.FromResult(sdp.ToString());
    }

    /// <inheritdoc />
    public async Task SetLocalDescriptionAsync(string sdp, CancellationToken ct = default)
    {
        var parsed = SDP.ParseSDPDescription(sdp);
        if (parsed is null)
        {
            throw new InvalidOperationException("Failed to parse local SDP");
        }
        var result = _inner.setLocalDescription(parsed);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setLocalDescription failed: {result}");
        }
    }

    /// <inheritdoc />
    public async Task SetRemoteDescriptionAsync(string sdp, bool isOffer, CancellationToken ct = default)
    {
        var parsed = SDP.ParseSDPDescription(sdp);
        if (parsed is null)
        {
            throw new InvalidOperationException("Failed to parse remote SDP");
        }
        var sdpType = isOffer ? SdpType.offer : SdpType.answer;
        var result = _inner.setRemoteDescription(sdpType, parsed);
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"setRemoteDescription failed: {result}");
        }
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken ct = default)
    {
        _inner.close();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _inner.close();
        return default;
    }

    /// <summary>
    /// 将 SIPSorcery 的 RTCPeerConnectionState 映射到 LocalSend 的枚举。
    /// </summary>
    private static RtcPeerConnectionState MapState(RTCPeerConnectionState state) => state switch
    {
        RTCPeerConnectionState.New => RtcPeerConnectionState.New,
        RTCPeerConnectionState.Connecting => RtcPeerConnectionState.Connecting,
        RTCPeerConnectionState.Connected => RtcPeerConnectionState.Connected,
        RTCPeerConnectionState.Disconnected => RtcPeerConnectionState.Disconnected,
        RTCPeerConnectionState.Failed => RtcPeerConnectionState.Failed,
        RTCPeerConnectionState.Closed => RtcPeerConnectionState.Closed,
        _ => RtcPeerConnectionState.Unspecified
    };
}
#endif