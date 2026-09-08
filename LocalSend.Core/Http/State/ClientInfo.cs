using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.State;

/// <summary>
/// 客户端（即本机）的设备信息，对应 Rust 端 <c>src/http/state.rs</c> 中的 <c>ClientInfo</c>。
/// </summary>
/// <remarks>
/// 与 <c>RegisterDto</c> 不同，这里不包含 port / protocol / hasWebInterface 等
/// 传输层字段，只描述设备自身。
/// </remarks>
public class ClientInfo
{
    /// <summary>设备显示名。</summary>
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    /// <summary>客户端协议版本（major.minor）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>设备型号，可选。</summary>
    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    /// <summary>设备类型，可选。</summary>
    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceType? DeviceType { get; set; }

    /// <summary>
    /// 客户端 token，用于在不同通道（LAN / WebRTC）间合并同一设备。
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}
