using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSend.Core.Http.DtoV2;

/// <summary>
/// v2 协议的传输协议类型，对应 Rust 端 <c>src/http/dto_v2.rs</c> 中的 <c>ProtocolTypeV2</c>。
/// 序列化使用小写形式（<c>"http"</c> / <c>"https"</c>）。
/// </summary>
[JsonConverter(typeof(ProtocolTypeV2JsonConverter))]
public enum ProtocolTypeV2
{
    Http,
    Https
}

public static class ProtocolTypeV2Extensions
{
    public static string AsStr(this ProtocolTypeV2 value) => value switch
    {
        ProtocolTypeV2.Http => "http",
        ProtocolTypeV2.Https => "https",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public sealed class ProtocolTypeV2JsonConverter : JsonConverter<ProtocolTypeV2>
{
    public override ProtocolTypeV2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToLowerInvariant() switch
        {
            "http" => ProtocolTypeV2.Http,
            "https" => ProtocolTypeV2.Https,
            _ => throw new JsonException($"Unknown ProtocolTypeV2: {value}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ProtocolTypeV2 value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.AsStr());
    }
}
