using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// 基于内存管道的 <see cref="IRtcPeerConnection"/> 实现。
/// 用于单元测试和本地调试，不依赖任何外部 WebRTC 库。
/// </summary>
/// <remarks>
/// 此实现通过 <see cref="Channel{T}"/> 在两个 peer connection 之间传递消息，
/// 模拟 WebRTC 数据通道的行为。仅适用于同一进程内的测试。
/// </remarks>
public sealed class LoopbackPeerConnection : IRtcPeerConnection
{
    private readonly ConcurrentDictionary<string, LoopbackDataChannel> _dataChannels = new();
    private readonly List<Action<IRtcDataChannel>> _onDataChannelCallbacks = new();
    private readonly List<Action<RtcPeerConnectionState>> _onStateChangeCallbacks = new();
    private readonly string _id;
    private bool _disposed;

    /// <summary>
    /// 获取用于连接另一端的"远端"引用。
    /// </summary>
    internal LoopbackPeerConnection? Remote { get; set; }

    public LoopbackPeerConnection()
    {
        _id = Guid.NewGuid().ToString("N");
    }

    public IRtcDataChannel CreateDataChannel(string label)
    {
        var dc = new LoopbackDataChannel(label, this);
        _dataChannels[label] = dc;
        return dc;
    }

    public void OnDataChannel(Action<IRtcDataChannel> callback)
    {
        _onDataChannelCallbacks.Add(callback);
    }

    public void OnPeerConnectionStateChange(Action<RtcPeerConnectionState> callback)
    {
        _onStateChangeCallbacks.Add(callback);
    }

    public Task<string> CreateOfferAsync(CancellationToken ct = default)
    {
        // 生成一个伪 SDP 字符串，格式保持 LocalSend 兼容
        var sdp = $"v=0\r\no=- {_id} 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=application 9 DTLS/SCTP 5000\r\na=app:data\r\n";
        return Task.FromResult(sdp);
    }

    public Task<string> CreateAnswerAsync(CancellationToken ct = default)
    {
        var sdp = $"v=0\r\no=- {_id} 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=application 9 DTLS/SCTP 5000\r\na=app:data\r\n";
        return Task.FromResult(sdp);
    }

    public Task SetLocalDescriptionAsync(string sdp, CancellationToken ct = default)
    {
        // 模拟 ICE 收集完成
        FireStateChange(RtcPeerConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task SetRemoteDescriptionAsync(string sdp, bool isOffer, CancellationToken ct = default)
    {
        // 模拟远端 SDP 设置完成
        FireStateChange(RtcPeerConnectionState.Connected);

        // 通知远端已创建的 data channel
        if (Remote is not null)
        {
            foreach (var dc in _dataChannels.Values)
            {
                var remoteDc = dc.CreateRemotePair();
                Remote.ReceiveDataChannel(remoteDc);
            }
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        FireStateChange(RtcPeerConnectionState.Closed);
        _disposed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            CloseAsync().Wait(TimeSpan.FromSeconds(5));
        }
        return default;
    }

    internal void ReceiveDataChannel(LoopbackDataChannel remoteChannel)
    {
        // 为远端的数据通道创建本地镜像
        var localMirror = remoteChannel.CreateLocalMirror();
        _dataChannels[remoteChannel.Label] = localMirror;

        // 触发 onDataChannel 回调
        foreach (var cb in _onDataChannelCallbacks)
        {
            cb(localMirror);
        }
    }

    private void FireStateChange(RtcPeerConnectionState state)
    {
        foreach (var cb in _onStateChangeCallbacks)
        {
            cb(state);
        }
    }
}

/// <summary>
/// Loopback 数据通道实现。
/// </summary>
public sealed class LoopbackDataChannel : IRtcDataChannel
{
    private Channel<RtcDataChannelMessage> _messages;
    private readonly TaskCompletionSource _openedTcs;
    private readonly LoopbackPeerConnection _owner;
    private readonly string _label;
    private LoopbackDataChannel? _pair;

    public string Label => _label;
    public Task Opened => _openedTcs.Task;
    public ChannelReader<RtcDataChannelMessage> Messages => _messages.Reader;

    internal LoopbackDataChannel(string label, LoopbackPeerConnection owner)
    {
        _label = label;
        _owner = owner;
        _messages = Channel.CreateUnbounded<RtcDataChannelMessage>();
        _openedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _openedTcs.TrySetResult(); // 立即打开（loopback）
    }

    public Task SendTextAsync(string text, CancellationToken ct = default)
    {
        var msg = new RtcDataChannelMessage(true, System.Text.Encoding.UTF8.GetBytes(text));
        _pair?._messages.Writer.TryWrite(msg);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(byte[] data, CancellationToken ct = default)
    {
        var msg = new RtcDataChannelMessage(false, data);
        _pair?._messages.Writer.TryWrite(msg);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        _messages.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task WaitBufferEmptyAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask; // loopback 无需等待
    }

    public ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        return default;
    }

    /// <summary>
    /// 为对端创建配对通道。
    /// </summary>
    internal LoopbackDataChannel CreateRemotePair()
    {
        var remote = new LoopbackDataChannel(_label, _owner)
        {
            _pair = this
        };
        _pair = remote;
        return remote;
    }

    /// <summary>
    /// 为远端的通道创建本地镜像（共享消息通道）。
    /// </summary>
    internal LoopbackDataChannel CreateLocalMirror()
    {
        var mirror = new LoopbackDataChannel(_label, _owner)
        {
            // 镜像使用同一个消息通道
            _messages = this._messages
        };
        return mirror;
    }
}

/// <summary>
/// <see cref="LoopbackPeerConnection"/> 工厂。
/// </summary>
public sealed class LoopbackPeerConnectionFactory : IRtcPeerConnectionFactory
{
    /// <summary>
    /// 创建两个互连的 loopback peer connection，用于测试。
    /// </summary>
    public static (LoopbackPeerConnection A, LoopbackPeerConnection B) CreatePair()
    {
        var a = new LoopbackPeerConnection();
        var b = new LoopbackPeerConnection();
        a.Remote = b;
        b.Remote = a;
        return (a, b);
    }

    public IRtcPeerConnection Create(IReadOnlyList<string> stunServers)
    {
        return new LoopbackPeerConnection();
    }
}