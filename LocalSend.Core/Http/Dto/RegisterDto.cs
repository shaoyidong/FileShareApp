using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Dto;

/// <summary>
/// v3 协议的设备注册请求，对应 Rust 端 <c>RegisterDto</c>。
/// 用于 <c>POST /api/localsend/v3/register</c>。
/// </summary>
public class RegisterDto
{
    /// <summary>设备显示名（例如 "My Phone"）。</summary>
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    /// <summary>协议版本（major.minor）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>设备型号，例如 "Samsung" / "Windows"。可选。</summary>
    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    /// <summary>设备类型，可选。</summary>
    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceType? DeviceType { get; set; }

    /// <summary>
    /// 客户端 token，用于在不同通道（LAN / WebRTC）间合并同一设备。
    /// 在 v3 中承载签名后的指纹。
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>监听端口。</summary>
    [JsonPropertyName("port")]
    public ushort Port { get; set; }

    /// <summary>使用 HTTP 还是 HTTPS。</summary>
    [JsonPropertyName("protocol")]
    public ProtocolType Protocol { get; set; }

    /// <summary>是否提供 Web 下载接口。默认 false。</summary>
    [JsonPropertyName("hasWebInterface")]
    public bool HasWebInterface { get; set; }
}

/// <summary>
/// v3 协议的设备注册响应，对应 Rust 端 <c>RegisterResponseDto</c>。
/// 类似 <see cref="RegisterDto"/> 但不包含 <c>port</c> 和 <c>protocol</c>
/// （这两个字段在 TCP 连接上已经知道）。
/// </summary>
public class RegisterResponseDto
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

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("hasWebInterface")]
    public bool HasWebInterface { get; set; }
}
