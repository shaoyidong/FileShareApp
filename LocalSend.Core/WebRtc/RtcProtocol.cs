using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LocalSend.Core.Util;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// WebRTC 数据通道协议层工具，对应 Rust 端 <c>src/webrtc/webrtc.rs</c> 中的
/// 自由函数（<c>encode_sdp</c> / <c>decode_sdp</c> / <c>process_in_chunks</c> /
/// <c>send_string_in_chunks</c> / <c>receive_string_from_chunks</c> /
/// <c>send_delimiter</c> / <c>is_delimiter</c> / <c>wait_buffer_empty</c> 等）。
/// </summary>
public static class RtcProtocol
{
    /// <summary>分块大小：16 KiB，对应 Rust 端 <c>CHUNK_SIZE</c>。</summary>
    public const int ChunkSize = 16 * 1024;

    /// <summary>
    /// SDP 字符串先 zlib 压缩、再做无填充 base64-url 编码。
    /// 对应 Rust 端 <c>encode_sdp</c>。
    /// </summary>
    public static string EncodeSdp(string sdp)
    {
        using var output = new MemoryStream();
        // ZLibStream 在 .NET 8 上对 Optimal 已能给出足够好的压缩比，
        // 对应 Rust 端 flate2::Compression::best()。
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(sdp);
            zlib.Write(bytes, 0, bytes.Length);
        }

        return Base64Url.Encode(output.ToArray());
    }

    /// <summary>
    /// 解码 SDP：先 base64-url 解码，再 zlib 解压。
    /// 对应 Rust 端 <c>decode_sdp</c>。
    /// </summary>
    public static string DecodeSdp(string encoded)
    {
        byte[] compressed = Base64Url.Decode(encoded);
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// 发送分隔符。对应 Rust 端 <c>send_delimiter</c>。
    /// 空消息会被部分实现丢弃，所以发一个非空字符串 <c>"0"</c>。
    /// </summary>
    public static Task SendDelimiterAsync(IRtcDataChannel channel, CancellationToken ct = default)
        => channel.SendTextAsync("0", ct);

    /// <summary>
    /// 判断消息是否为分隔符：文本消息且长度 &lt;= 1。
    /// 对应 Rust 端 <c>is_delimiter</c>。
    /// </summary>
    public static bool IsDelimiter(in RtcDataChannelMessage msg)
        => msg.IsString && msg.Data.Length <= 1;

    /// <summary>
    /// 把字节流通道里的数据按 <see cref="ChunkSize"/> 切块，逐块通过
    /// <paramref name="sendChunk"/> 发送。对应 Rust 端 <c>process_in_chunks</c>。
    /// </summary>
    /// <remarks>
    /// 调用方需要在最后再调用 <see cref="SendDelimiterAsync"/> 表示结束。
    /// </remarks>
    public static async Task ProcessInChunksAsync(
        ChannelReader<byte[]> input,
        Func<byte[], CancellationToken, Task> sendChunk,
        CancellationToken ct = default)
    {
        var buffer = new List<byte>(ChunkSize);
        // 复用一块字节数组避免反复分配。
        var pending = new byte[ChunkSize];
        int pendingLen = 0;

        await foreach (var data in input.ReadAllAsync(ct))
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int copy = Math.Min(ChunkSize - pendingLen, data.Length - offset);
                Buffer.BlockCopy(data, offset, pending, pendingLen, copy);
                pendingLen += copy;
                offset += copy;

                if (pendingLen == ChunkSize)
                {
                    await sendChunk(pending, ct);
                    pendingLen = 0;
                }
            }
        }

        if (pendingLen > 0)
        {
            // 最后一块不足 CHUNK_SIZE，按实际长度裁剪。
            var tail = new byte[pendingLen];
            Buffer.BlockCopy(pending, 0, tail, 0, pendingLen);
            await sendChunk(tail, ct);
        }
    }

    /// <summary>
    /// 把单个字符串按 <see cref="ChunkSize"/> 切成二进制块发送。
    /// 对应 Rust 端 <c>send_string_in_chunks</c>。
    /// 调用方需要在最后再调用 <see cref="SendDelimiterAsync"/>。
    /// </summary>
    public static async Task SendStringInChunksAsync(
        IRtcDataChannel channel,
        string text,
        CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        // 用一个一次性通道把字节喂给 ProcessInChunksAsync。
        var ch = Channel.CreateBounded<byte[]>(1);
        await ch.Writer.WriteAsync(bytes, ct);
        ch.Writer.TryComplete();

        await ProcessInChunksAsync(
            ch.Reader,
            (chunk, token) => channel.SendBinaryAsync(chunk, token),
            ct);
    }

    /// <summary>
    /// 从数据通道接收二进制块直到遇到文本消息（分隔符或下一个 JSON 头），
    /// 把它们拼接成完整字节序列。对应 Rust 端 <c>receive_string_from_chunks</c>。
    /// </summary>
    /// <remarks>
    /// 与 Rust 端语义一致：遇到 <c>is_string</c> 消息即停止，
    /// 调用方需要根据需要单独处理那条文本消息。
    /// 这里把拼接好的字节返回，遇到的首条文本消息也通过
    /// <paramref name="terminator"/> 返回（可能为 <c>null</c> 表示通道关闭）。
    /// </remarks>
    public static async Task<(byte[] Data, RtcDataChannelMessage? Terminator)> ReceiveBinaryUntilTextAsync(
        ChannelReader<RtcDataChannelMessage> input,
        CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        RtcDataChannelMessage? terminator = null;
        while (await input.WaitToReadAsync(ct))
        {
            while (input.TryRead(out var msg))
            {
                if (msg.IsString)
                {
                    terminator = msg;
                    return (buffer.ToArray(), terminator);
                }

                buffer.Write(msg.Data, 0, msg.Data.Length);
            }
        }

        return (buffer.ToArray(), null);
    }

    /// <summary>
    /// 发送一条 JSON 文本消息。
    /// </summary>
    public static Task SendJsonAsync<T>(
        IRtcDataChannel channel,
        T value,
        CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(value);
        return channel.SendTextAsync(json, ct);
    }
}
