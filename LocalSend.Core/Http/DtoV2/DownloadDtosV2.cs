using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.DtoV2;

/// <summary>
/// v2.1 协议的 prepare-download 响应，对应 Rust 端 <c>PrepareDownloadResponseDtoV2</c>。
/// 用于 <c>POST /api/localsend/v2/prepare-download</c>。
/// </summary>
public class PrepareDownloadResponseDtoV2
{
    /// <summary>发送端（提供下载的一端）的设备信息。</summary>
    [JsonPropertyName("info")]
    public InfoResponseDtoV2 Info { get; set; } = new();

    /// <summary>会话 ID。</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>可下载的文件，键为 file ID。</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, FileDto> Files { get; set; } = new();
}

/// <summary>
/// v2.1 协议的 info 响应，对应 Rust 端 <c>InfoResponseDtoV2</c>。
/// 用于 <c>GET /api/localsend/v2/info</c>，也作为 prepare-download 响应中的 info 字段。
/// </summary>
public class InfoResponseDtoV2
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

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("download")]
    public bool Download { get; set; }
}
