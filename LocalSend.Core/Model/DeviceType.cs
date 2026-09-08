using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSend.Core.Model;

/// <summary>
/// 设备类型，对应 Rust 端 <c>src/model/discovery.rs</c> 中的 <c>DeviceType</c> 枚举。
/// </summary>
/// <remarks>
/// v3 协议在传输时使用 <c>SCREAMING_SNAKE_CASE</c>（例如 <c>"MOBILE"</c>、<c>"DESKTOP"</c>）。
/// v2 协议则使用小写形式，由 <see cref="Http.DtoV2"/> 模块中的自定义转换器处理。
/// </remarks>
[JsonConverter(typeof(DeviceTypeJsonConverter))]
public enum DeviceType
{
    /// <summary>移动设备（手机、平板等）。</summary>
    Mobile,

    /// <summary>桌面设备（Windows / macOS / Linux 桌面端）。</summary>
    Desktop,

    /// <summary>浏览器 Web 端。</summary>
    Web,

    /// <summary>无头设备（无 GUI 的服务器或嵌入式设备）。</summary>
    Headless,

    /// <summary>文件服务器。</summary>
    Server
}

/// <summary>
/// 将 <see cref="DeviceType"/> 序列化为 v3 协议使用的 <c>SCREAMING_SNAKE_CASE</c>。
/// 对应 Rust 的 <c>#[serde(rename_all = "SCREAMING_SNAKE_CASE")]</c>。
/// </summary>
public sealed class DeviceTypeJsonConverter : JsonConverter<DeviceType>
{
    public override DeviceType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToUpperInvariant() switch
        {
            "MOBILE" => DeviceType.Mobile,
            "DESKTOP" => DeviceType.Desktop,
            "WEB" => DeviceType.Web,
            "HEADLESS" => DeviceType.Headless,
            "SERVER" => DeviceType.Server,
            // 未知值回退到 Desktop，符合协议（参见 v2 协议 7.1 节）。
            _ => DeviceType.Desktop
        };
    }

    public override void Write(Utf8JsonWriter writer, DeviceType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            DeviceType.Mobile => "MOBILE",
            DeviceType.Desktop => "DESKTOP",
            DeviceType.Web => "WEB",
            DeviceType.Headless => "HEADLESS",
            DeviceType.Server => "SERVER",
            _ => "DESKTOP"
        });
    }
}
