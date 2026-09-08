using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.DtoV2;

/// <summary>
/// v2.1 协议下的 UDP 多播发现消息，对应 Rust 端 <c>MulticastMessageV2</c>。
/// </summary>
/// <remarks>
/// 既用于发送 announce，也用于响应 announce。
/// <c>Announce == true</c> 时其他设备应当回复；
/// <c>Announce == false</c> 时表示这是对 announce 的回复。
/// </remarks>
public class MulticastMessageV2
{
    /// <summary>设备显示名。</summary>
    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;

    /// <summary>协议版本（例如 "2.1"）。</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>设备型号，可选。</summary>
    [JsonPropertyName("deviceModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceModel { get; set; }

    /// <summary>
    /// 设备类型，可选。
    /// 使用 v2 风格的小写序列化。
    /// </summary>
    [JsonPropertyName("deviceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(DeviceTypeV2JsonConverter))]
    public DeviceType? DeviceType { get; set; }

    /// <summary>
    /// 设备指纹：
    /// HTTPS 模式下为证书的 SHA-256；
    /// HTTP 模式下为随机字符串。
    /// </summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>监听端口。</summary>
    [JsonPropertyName("port")]
    public ushort Port { get; set; }

    /// <summary>使用 http 还是 https。</summary>
    [JsonPropertyName("protocol")]
    public ProtocolTypeV2 Protocol { get; set; }

    /// <summary>是否启用 Download API（协议 5.2、5.3）。</summary>
    [JsonPropertyName("download")]
    public bool Download { get; set; }

    /// <summary>true 表示这是 announce；false 表示这是对 announce 的响应。</summary>
    [JsonPropertyName("announce")]
    public bool Announce { get; set; }

    /// <summary>
    /// 旧版兼容字段（v1 协议使用 "announcement"）。
    /// 序列化时与 Announce 同步，反序列化时两者任一为 true 即视为 announce。
    /// </summary>
    [JsonPropertyName("announcement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Announcement { get; set; }
}
