using System.Text.Json;
using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.DtoV2;

/// <summary>
/// v2 协议下 <see cref="DeviceType"/> 的 JSON 转换器，对应 Rust 端
/// <c>dto_v2.rs</c> 中的 <c>device_type_v2</c> serde 模块。
/// </summary>
/// <remarks>
/// v2 协议在传输时使用小写形式（<c>"mobile"</c>、<c>"desktop"</c> 等），
/// 未知值回退到 <see cref="DeviceType.Desktop"/>（协议 7.1 节）。
/// </remarks>
public sealed class DeviceTypeV2JsonConverter : JsonConverter<DeviceType?>
{
    public override DeviceType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        var value = reader.GetString();
        return value?.ToLowerInvariant() switch
        {
            "mobile" => DeviceType.Mobile,
            "desktop" => DeviceType.Desktop,
            "web" => DeviceType.Web,
            "headless" => DeviceType.Headless,
            "server" => DeviceType.Server,
            _ => DeviceType.Desktop // 未知值回退到 Desktop。
        };
    }

    public override void Write(Utf8JsonWriter writer, DeviceType? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStringValue(value.Value switch
        {
            DeviceType.Mobile => "mobile",
            DeviceType.Desktop => "desktop",
            DeviceType.Web => "web",
            DeviceType.Headless => "headless",
            DeviceType.Server => "server",
            _ => "desktop"
        });
    }
}
