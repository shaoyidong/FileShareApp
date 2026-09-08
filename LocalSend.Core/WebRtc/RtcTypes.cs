using System.Threading.Channels;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// WebRTC 数据通道的状态，对应 Rust 端
/// <c>src/webrtc/webrtc.rs</c> 中的 <c>RTCStatus</c> 枚举。
/// </summary>
/// <remarks>
/// 这些状态会通过 <see cref="Channel{RTCStatus}"/> 推给上层应用，
/// 用来驱动 UI（例如显示"等待 PIN"、"传输中"、"被拒绝"等）。
/// </remarks>
public abstract class RtcStatus
{
    /// <summary>已交换 SDP（offer / answer），P2P 连接开始建立。</summary>
    public sealed class SdpExchanged : RtcStatus { }

    /// <summary>数据通道已打开，可开始后续协商。</summary>
    public sealed class Connected : RtcStatus { }

    /// <summary>需要 PIN 才能继续。</summary>
    public sealed class PinRequired : RtcStatus { }

    /// <summary>尝试次数过多，连接已关闭。</summary>
    public sealed class TooManyAttempts : RtcStatus { }

    /// <summary>被接收端拒绝。</summary>
    public sealed class Declined : RtcStatus { }

    /// <summary>正在发送文件。</summary>
    public sealed class Sending : RtcStatus { }

    /// <summary>数据通道关闭，传输完成。</summary>
    public sealed class Finished : RtcStatus { }

    /// <summary>出错，连接已关闭。<see cref="Error.ErrorMessage"/> 描述具体错误。</summary>
    public sealed class Error : RtcStatus
    {
        public string ErrorMessage { get; init; } = string.Empty;
    }
}

/// <summary>
/// 单个文件传输的错误，对应 Rust 端 <c>RTCFileError</c>。
/// </summary>
public sealed class RtcFileError
{
    /// <summary>出错的文件 ID。</summary>
    public string FileId { get; init; } = string.Empty;

    /// <summary>错误描述。</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// PIN 配置：用户输入的 PIN 以及允许的最大尝试次数，
/// 对应 Rust 端 <c>PinConfig</c>。
/// </summary>
public sealed class PinConfig
{
    public string Pin { get; init; } = string.Empty;
    public byte MaxTries { get; init; }

    public PinConfig(string pin, byte maxTries)
    {
        Pin = pin;
        MaxTries = maxTries;
    }
}

/// <summary>
/// 一个待发送的文件：除了文件 ID，还带一个 <see cref="Channel{Byte[]}"/>，
/// 上层通过它把文件字节流分块喂给 WebRTC 通道。
/// 对应 Rust 端 <c>RTCFile</c>（注意 Rust 端用的是 <c>Bytes</c>，这里用 <c>byte[]</c>）。
/// </summary>
public sealed class RtcFile
{
    /// <summary>文件 ID。</summary>
    public string FileId { get; init; } = string.Empty;

    /// <summary>
    /// 文件字节流通道：上层向其写入字节块，发送端循环读出并通过数据通道发出。
    /// 关闭该通道表示该文件已读完。
    /// </summary>
    public Channel<byte[]> BinaryRx { get; init; } = Channel.CreateUnbounded<byte[]>();
}

/// <summary>
/// 接收端单文件状态，对应 Rust 端 <c>RTCFileState</c>（仅内部使用）。
/// </summary>
internal sealed class RtcFileState
{
    public string FileId { get; init; } = string.Empty;
    public ulong Size { get; init; }
    public ulong Received { get; set; }
    public Channel<byte[]> BinaryTx { get; init; } = Channel.CreateBounded<byte[]>(4);
}
