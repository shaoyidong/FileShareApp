using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSend.Core.WebRtc.Signaling;

/// <summary>
/// 信令服务器 -> 客户端的消息，对应 Rust 端 <c>WsServerMessage</c>。
/// </summary>
/// <remarks>
/// 使用 <c>[serde(tag = "type", rename_all = "SCREAMING_SNAKE_CASE")]</c> 内部标签风格，
/// 即 JSON 中会有 <c>"type": "HELLO"</c> / <c>"OFFER"</c> 等字段。
/// C# 端通过自定义 <see cref="JsonConverter"/> 实现等价的 discriminated union 行为。
/// </remarks>
[JsonConverter(typeof(WsServerMessageConverter))]
public abstract class WsServerMessage
{
    /// <summary>服务器对刚连接的客户端发送的首条消息。</summary>
    public sealed class Hello : WsServerMessage
    {
        public WsClientInfo Client { get; set; } = new();
        public List<WsClientInfo> Peers { get; set; } = new();
    }

    /// <summary>新对端加入。</summary>
    public sealed class Join : WsServerMessage
    {
        public WsClientInfo Peer { get; set; } = new();
    }

    /// <summary>对端更新了自身信息。</summary>
    public sealed class Update : WsServerMessage
    {
        public WsClientInfo Peer { get; set; } = new();
    }

    /// <summary>对端离开。</summary>
    public sealed class Left : WsServerMessage
    {
        [JsonPropertyName("peerId")]
        public Guid PeerId { get; set; }
    }

    /// <summary>对端发来的 SDP offer。</summary>
    public sealed class Offer : WsServerMessage
    {
        public WsServerSdpMessage Sdp { get; set; } = new();
    }

    /// <summary>对端发来的 SDP answer。</summary>
    public sealed class Answer : WsServerMessage
    {
        public WsServerSdpMessage Sdp { get; set; } = new();
    }

    /// <summary>错误码。</summary>
    public sealed class Error : WsServerMessage
    {
        public ushort Code { get; set; }
    }
}

/// <summary>
/// SDP 描述（offer / answer 共用），对应 Rust 端 <c>WsServerSdpMessage</c>。
/// </summary>
public class WsServerSdpMessage
{
    public WsClientInfo Peer { get; set; } = new();

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>SDP 字符串：zlib 压缩后做无填充 base64-url 编码。</summary>
    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;
}

/// <summary>
/// 客户端 -> 信令服务器的消息，对应 Rust 端 <c>WsClientMessage</c>。
/// </summary>
[JsonConverter(typeof(WsClientMessageConverter))]
public abstract class WsClientMessage
{
    public sealed class Update : WsClientMessage
    {
        public WsClientInfoWithoutId Info { get; set; } = new();
    }

    public sealed class Offer : WsClientMessage
    {
        public WsClientSdpMessage Sdp { get; set; } = new();
    }

    public sealed class Answer : WsClientMessage
    {
        public WsClientSdpMessage Sdp { get; set; } = new();
    }
}

/// <summary>
/// 客户端发送的 SDP 描述，对应 Rust 端 <c>WsClientSdpMessage</c>。
/// </summary>
public class WsClientSdpMessage
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>目标对端 ID。</summary>
    [JsonPropertyName("target")]
    public Guid Target { get; set; }

    /// <summary>SDP 字符串（zlib + base64-url）。</summary>
    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;
}

/// <summary>
/// <see cref="WsServerMessage"/> 的 JSON 转换器，模拟 Rust 的
/// <c>#[serde(tag = "type", rename_all = "SCREAMING_SNAKE_CASE")]</c>。
/// </summary>
internal sealed class WsServerMessageConverter : JsonConverter<WsServerMessage>
{
    public override WsServerMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl))
        {
            throw new JsonException("Missing 'type' tag");
        }
        var type = typeEl.GetString();
        // 读取时把 "type" 字段去掉再反序列化为具体子类，
        // 否则子类没有该字段会报错。
        var obj = root.Clone();
        return type?.ToUpperInvariant() switch
        {
            "HELLO" => obj.Deserialize<WsServerMessage.Hello>(options),
            "JOIN" => obj.Deserialize<WsServerMessage.Join>(options),
            "UPDATE" => obj.Deserialize<WsServerMessage.Update>(options),
            "LEFT" => obj.Deserialize<WsServerMessage.Left>(options),
            "OFFER" => obj.Deserialize<WsServerMessage.Offer>(options),
            "ANSWER" => obj.Deserialize<WsServerMessage.Answer>(options),
            "ERROR" => obj.Deserialize<WsServerMessage.Error>(options),
            _ => throw new JsonException($"Unknown WsServerMessage type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, WsServerMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value switch
        {
            WsServerMessage.Hello => "HELLO",
            WsServerMessage.Join => "JOIN",
            WsServerMessage.Update => "UPDATE",
            WsServerMessage.Left => "LEFT",
            WsServerMessage.Offer => "OFFER",
            WsServerMessage.Answer => "ANSWER",
            WsServerMessage.Error => "ERROR",
            _ => throw new JsonException("Unknown WsServerMessage subtype")
        });
        // 把子类字段写入。这里直接序列化子类，但跳过它本身没有的 type。
        var subOptions = new JsonSerializerOptions(options);
        // 使用源生成会更优雅；此处简单序列化每个子类并合并字段。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), subOptions);
        using var sub = JsonDocument.Parse(bytes);
        foreach (var prop in sub.RootElement.EnumerateObject())
        {
            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}

internal sealed class WsClientMessageConverter : JsonConverter<WsClientMessage>
{
    public override WsClientMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl))
        {
            throw new JsonException("Missing 'type' tag");
        }
        var type = typeEl.GetString();
        var obj = root.Clone();
        return type?.ToUpperInvariant() switch
        {
            "UPDATE" => obj.Deserialize<WsClientMessage.Update>(options),
            "OFFER" => obj.Deserialize<WsClientMessage.Offer>(options),
            "ANSWER" => obj.Deserialize<WsClientMessage.Answer>(options),
            _ => throw new JsonException($"Unknown WsClientMessage type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, WsClientMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value switch
        {
            WsClientMessage.Update => "UPDATE",
            WsClientMessage.Offer => "OFFER",
            WsClientMessage.Answer => "ANSWER",
            _ => throw new JsonException("Unknown WsClientMessage subtype")
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
        using var sub = JsonDocument.Parse(bytes);
        foreach (var prop in sub.RootElement.EnumerateObject())
        {
            prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
