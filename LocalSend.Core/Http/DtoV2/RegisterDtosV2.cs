using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.DtoV2;

/// <summary>
/// v2.1 协议的注册请求，对应 Rust 端 <c>RegisterDtoV2</c>。
/// 用于 <c>POST /api/localsend/v2/register</c>。
/// </summary>
public class RegisterDtoV2
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(DeviceTypeV2JsonConverter))]
    public DeviceType? DeviceType { get; set; }

    /// <summary>设备指纹。HTTPS 模式下被忽略（使用证书指纹）。</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public ushort Port { get; set; }

    [JsonPropertyName("protocol")]
    public ProtocolTypeV2 Protocol { get; set; }

    /// <summary>是否启用 Download API。默认 false。</summary>
    [JsonPropertyName("download")]
    public bool Download { get; set; }
}

/// <summary>
/// v2.1 协议的注册响应，对应 Rust 端 <c>RegisterResponseDtoV2</c>。
/// 响应自 <c>POST /api/localsend/v2/register</c>。
/// </summary>
public class RegisterResponseDtoV2
{
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(DeviceTypeV2JsonConverter))]
    public DeviceType? DeviceType { get; set; }

    /// <summary>设备指纹。HTTPS 模式下被忽略。</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>是否启用 Download API。默认 false。</summary>
    [JsonPropertyName("download")]
    public bool Download { get; set; }
}
