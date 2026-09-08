using System.Text.Json;
using System.Text.Json.Serialization;
using LocalSend.Core.Model;
using LocalSend.Core.Util;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// WebRTC 数据通道内交换的 JSON 消息集合，对应 Rust 端
/// <c>src/webrtc/webrtc.rs</c> 顶部的 <c>RTC*</c> 系列结构 / 枚举。
/// </summary>
/// <remarks>
/// 这些消息只走 WebRTC 数据通道（不走信令服务器），
/// 用于双方协商 nonce / token / PIN / 文件列表 / 配对 / 单文件结果。
/// 大部分状态枚举使用 Rust 的 <c>#[serde(tag = "status", rename_all = "SCREAMING_SNAKE_CASE")]</c>，
/// C# 端通过自定义 <see cref="JsonConverter"/> 模拟该行为。
/// </remarks>

/// <summary>
/// Nonce 协商消息：双方各发一条，发起方先发。对应 Rust 端 <c>RTCNonceMessage</c>。
/// </summary>
public sealed class RtcNonceMessage
{
    /// <summary>
    /// 用于与公钥一起做哈希的 nonce，URL 安全、无填充 base64 编码。
    /// </summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    /// <summary>从字节数组构造，自动做 base64-url 编码。</summary>
    public static RtcNonceMessage FromBytes(byte[] nonce) => new()
    {
        Nonce = Base64Url.Encode(nonce)
    };

    /// <summary>解码为字节数组。</summary>
    public byte[] DecodeNonce() => Base64Url.Decode(Nonce);
}

/// <summary>
/// 发送端发来的 token 请求，对应 Rust 端 <c>RTCTokenRequest</c>。
/// </summary>
public sealed class RtcTokenRequest
{
    /// <summary>
    /// 使用收到的 nonce 和发送端私钥生成的 token，格式：
    /// <c>HASH_METHOD.HASH.SIGN_METHOD.SIGN</c>。
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// 接收端返回的 token 响应，对应 Rust 端 <c>RTCTokenResponse</c>。
/// </summary>
/// <remarks>
/// 序列化形式：<c>{"status":"OK","token":"..."}</c> /
/// <c>{"status":"PIN_REQUIRED","token":"..."}</c> / <c>{"status":"INVALID_SIGNATURE"}</c>。
/// </remarks>
[JsonConverter(typeof(RtcTokenResponseConverter))]
public abstract class RtcTokenResponse
{
    /// <summary>Token 验证通过，可能附带 PIN 挑战。</summary>
    public sealed class Ok : RtcTokenResponse
    {
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>Token 通过但需要 PIN 才能继续。</summary>
    public sealed class PinRequired : RtcTokenResponse
    {
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>Token 签名验证失败，连接将被对端关闭。</summary>
    public sealed class InvalidSignature : RtcTokenResponse { }
}

/// <summary>
/// PIN 消息：双方都可能发，对应 Rust 端 <c>RTCPinMessage</c>。
/// </summary>
public sealed class RtcPinMessage
{
    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;
}

/// <summary>
/// 接收端对 PIN 的响应，对应 Rust 端 <c>RTCPinReceivingResponse</c>。
/// </summary>
[JsonConverter(typeof(RtcPinReceivingResponseConverter))]
public abstract class RtcPinReceivingResponse
{
    public sealed class Ok : RtcPinReceivingResponse { }
    public sealed class PinRequired : RtcPinReceivingResponse { }
    public sealed class TooManyAttempts : RtcPinReceivingResponse { }
}

/// <summary>
/// 发送端对 PIN 的响应，对应 Rust 端 <c>RTCPinSendingResponse</c>。
/// 当 <see cref="Ok"/> 时会附带文件列表。
/// </summary>
[JsonConverter(typeof(RtcPinSendingResponseConverter))]
public abstract class RtcPinSendingResponse
{
    public sealed class Ok : RtcPinSendingResponse
    {
        public List<FileDto> Files { get; set; } = new();
    }

    public sealed class PinRequired : RtcPinSendingResponse { }
    public sealed class TooManyAttempts : RtcPinSendingResponse { }
}

/// <summary>
/// 接收端在收到 <see cref="RtcPinSendingResponse.Ok"/> 后发出的文件列表响应，
/// 对应 Rust 端 <c>RTCFileListResponse</c>。
/// </summary>
[JsonConverter(typeof(RtcFileListResponseConverter))]
public abstract class RtcFileListResponse
{
    /// <summary>接收端接受下载，附带 file_id -&gt; token 的映射。</summary>
    public sealed class Ok : RtcFileListResponse
    {
        public Dictionary<string, string> Files { get; set; } = new();
    }

    /// <summary>接收端发起配对请求，附带自己公钥的 PEM 字符串。</summary>
    public sealed class Pair : RtcFileListResponse
    {
        [JsonPropertyName("publicKey")]
        public string PublicKey { get; set; } = string.Empty;
    }

    /// <summary>接收端拒绝接收。</summary>
    public sealed class Declined : RtcFileListResponse { }

    /// <summary>对端公钥签名验证失败。</summary>
    public sealed class InvalidSignature : RtcFileListResponse { }
}

/// <summary>
/// 发送端对配对请求的响应，对应 Rust 端 <c>RTCPairResponse</c>。
/// </summary>
[JsonConverter(typeof(RtcPairResponseConverter))]
public abstract class RtcPairResponse
{
    public sealed class Ok : RtcPairResponse
    {
        [JsonPropertyName("publicKey")]
        public string PublicKey { get; set; } = string.Empty;
    }

    public sealed class PairDeclined : RtcPairResponse { }
    public sealed class InvalidSignature : RtcPairResponse { }
}

/// <summary>
/// 发送端开始传文件前发的请求头，对应 Rust 端 <c>RTCSendFileHeaderRequest</c>。
/// </summary>
public sealed class RtcSendFileHeaderRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// 单个文件传输完成后的结果，对应 Rust 端 <c>RTCSendFileResponse</c>。
/// </summary>
public sealed class RtcSendFileResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

// ----------------------------------------------------------------------------
// JSON 转换器：把 Rust 的 #[serde(tag = "status", rename_all = "SCREAMING_SNAKE_CASE")]
// 行为映射到 C# 的类层级。
// ----------------------------------------------------------------------------

internal sealed class RtcTokenResponseConverter : TaggedUnionConverter<RtcTokenResponse>
{
    public RtcTokenResponseConverter()
        : base("status", new (string, Func<RtcTokenResponse>)[]
        {
            ("OK", () => new RtcTokenResponse.Ok()),
            ("PIN_REQUIRED", () => new RtcTokenResponse.PinRequired()),
            ("INVALID_SIGNATURE", () => new RtcTokenResponse.InvalidSignature()),
        }) {}
}

internal sealed class RtcPinReceivingResponseConverter : TaggedUnionConverter<RtcPinReceivingResponse>
{
    public RtcPinReceivingResponseConverter()
        : base("status", new (string, Func<RtcPinReceivingResponse>)[]
        {
            ("OK", () => new RtcPinReceivingResponse.Ok()),
            ("PIN_REQUIRED", () => new RtcPinReceivingResponse.PinRequired()),
            ("TOO_MANY_ATTEMPTS", () => new RtcPinReceivingResponse.TooManyAttempts()),
        }) {}
}

internal sealed class RtcPinSendingResponseConverter : TaggedUnionConverter<RtcPinSendingResponse>
{
    public RtcPinSendingResponseConverter()
        : base("status", new (string, Func<RtcPinSendingResponse>)[]
        {
            ("OK", () => new RtcPinSendingResponse.Ok()),
            ("PIN_REQUIRED", () => new RtcPinSendingResponse.PinRequired()),
            ("TOO_MANY_ATTEMPTS", () => new RtcPinSendingResponse.TooManyAttempts()),
        }) {}
}

internal sealed class RtcFileListResponseConverter : TaggedUnionConverter<RtcFileListResponse>
{
    public RtcFileListResponseConverter()
        : base("status", new (string, Func<RtcFileListResponse>)[]
        {
            ("OK", () => new RtcFileListResponse.Ok()),
            ("PAIR", () => new RtcFileListResponse.Pair()),
            ("DECLINED", () => new RtcFileListResponse.Declined()),
            ("INVALID_SIGNATURE", () => new RtcFileListResponse.InvalidSignature()),
        }) {}
}

internal sealed class RtcPairResponseConverter : TaggedUnionConverter<RtcPairResponse>
{
    public RtcPairResponseConverter()
        : base("status", new (string, Func<RtcPairResponse>)[]
        {
            ("OK", () => new RtcPairResponse.Ok()),
            ("PAIR_DECLINED", () => new RtcPairResponse.PairDeclined()),
            ("INVALID_SIGNATURE", () => new RtcPairResponse.InvalidSignature()),
        }) {}
}

/// <summary>
/// 通用的"内部标签"discriminated union 转换器，对应 Rust 的
/// <c>#[serde(tag = "...", rename_all = "SCREAMING_SNAKE_CASE")]</c>。
/// </summary>
/// <typeparam name="T">抽象基类类型。</typeparam>
internal abstract class TaggedUnionConverter<T> : JsonConverter<T> where T : class
{
    private readonly string _tagField;
    private readonly Dictionary<string, Func<T>> _factory;

    /// <param name="tagField">标签字段名，例如 <c>"status"</c>。</param>
    /// <param name="variants">标签值 -> 子类工厂。</param>
    protected TaggedUnionConverter(string tagField, (string Tag, Func<T> Factory)[] variants)
    {
        _tagField = tagField;
        _factory = new(variants.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, factory) in variants)
        {
            _factory[tag] = factory;
        }
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty(_tagField, out var tagEl))
        {
            throw new JsonException($"Missing '{_tagField}' tag");
        }

        var tag = tagEl.GetString();
        if (tag is null || !_factory.TryGetValue(tag, out var factory))
        {
            throw new JsonException($"Unknown {_tagField} tag: {tag}");
        }

        // 反序列化到具体子类。
        return (T?)root.Deserialize(factory().GetType(), options);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // 找到 value 对应的标签。通过遍历 _factory 找到第一个工厂返回类型与 value 类型相同者。
        string? tag = null;
        foreach (var (key, factory) in _factory)
        {
            if (factory().GetType() == value.GetType())
            {
                tag = key;
                break;
            }
        }

        if (tag is null)
        {
            throw new JsonException($"Unknown subtype: {value.GetType()}");
        }

        writer.WriteStartObject();
        writer.WriteString(_tagField, tag);
        // 把子类字段写入（排除 _tagField 本身）。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
        using var sub = JsonDocument.Parse(bytes);
        foreach (var prop in sub.RootElement.EnumerateObject())
        {
            if (prop.NameEquals(_tagField))
            {
                continue;
            }

            prop.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
