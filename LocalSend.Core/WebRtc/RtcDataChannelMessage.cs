namespace LocalSend.Core.WebRtc;

/// <summary>
/// 一条来自对端的数据通道消息，对应 Rust 端 <c>DataChannelMessage</c>。
/// </summary>
public readonly record struct RtcDataChannelMessage(bool IsString, byte[] Data)
{
    /// <summary>若是文本消息，按 UTF-8 解码后的字符串。</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Data);
}
