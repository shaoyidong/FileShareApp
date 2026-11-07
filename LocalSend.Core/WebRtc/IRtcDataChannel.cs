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
