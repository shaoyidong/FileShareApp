using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSend.Core.Http.Dto;

/// <summary>
/// v3 协议使用的传输协议类型，对应 Rust 端 <c>src/http/dto.rs</c> 中的 <c>ProtocolType</c>。
/// </summary>
/// <remarks>
/// 序列化使用大写蛇形（<c>SCREAMING_SNAKE_CASE</c>），例如 <c>"HTTP"</c> / <c>"HTTPS"</c>。
/// </remarks>
[JsonConverter(typeof(ProtocolTypeJsonConverter))]
public enum ProtocolType
{
    /// <summary>明文 HTTP。</summary>
    Http,

    /// <summary>带 mTLS 的 HTTPS（LocalSend 默认）。</summary>
    Https
}

/// <summary>
/// v3 协议 ProtocolType 的 JSON 转换器，对应 Rust 端
/// <c>#[serde(rename_all = "SCREAMING_SNAKE_CASE")]</c>。
/// </summary>
public sealed class ProtocolTypeJsonConverter : JsonConverter<ProtocolType>
{
    public override ProtocolType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToUpperInvariant() switch
        {
            "HTTP" => ProtocolType.Http,
            "HTTPS" => ProtocolType.Https,
            _ => throw new JsonException($"Unknown ProtocolType: {value}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ProtocolType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ProtocolType.Http => "HTTP",
            ProtocolType.Https => "HTTPS",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        });
    }
}

/// <summary>v3 协议 ProtocolType 的扩展方法。</summary>
public static class ProtocolTypeExtensions
{
    /// <summary>返回协议字符串（小写 <c>"http"</c> / <c>"https"</c>），用于构造 URL。</summary>
    public static string AsStr(this ProtocolType value) => value switch
    {
        ProtocolType.Http => "http",
        ProtocolType.Https => "https",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
