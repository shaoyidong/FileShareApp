using System.Text.Json;
using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.WebRtc.Signaling;

/// <summary>
/// WebRTC 信令通道中的客户端信息，对应 Rust 端
/// <c>src/webrtc/signaling.rs</c> 中的 <c>ClientInfo</c>（含 id 字段）。
/// </summary>
public class WsClientInfo
{
    /// <summary>由服务器分配的对端 ID。</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceType? DeviceType { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    public static WsClientInfo From(WsClientInfoWithoutId info, Guid id) => new()
    {
        Id = id,
        Alias = info.Alias,
        Version = info.Version,
        DeviceModel = info.DeviceModel,
        DeviceType = info.DeviceType,
        Token = info.Token
    };
}

/// <summary>
/// 不含 id 的客户端信息，对应 Rust 端 <c>ClientInfoWithoutId</c>。
/// 客户端连接信令服务器时把它编码为 base64 放到 URL 查询参数 <c>d=</c> 中。
/// </summary>
public class WsClientInfoWithoutId
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
    public DeviceType? DeviceType { get; set; }

    /// <summary>客户端生成的指纹，用于跨通道合并同一设备。</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    public static WsClientInfoWithoutId From(WsClientInfo info) => new()
    {
        Alias = info.Alias,
        Version = info.Version,
        DeviceModel = info.DeviceModel,
        DeviceType = info.DeviceType,
        Token = info.Token
    };
}
