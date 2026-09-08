using System.Text.Json.Serialization;

namespace LocalSend.Core.Model;

/// <summary>
/// 文件描述，对应 Rust 端 <c>src/model/transfer.rs</c> 中的 <c>FileDto</c>。
/// 在 prepare-upload / prepare-download 等请求里描述单个待传输文件。
/// </summary>
public class FileDto
{
    /// <summary>文件 ID（在单个会话内唯一），由发送端分配。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>文件名（可能包含相对路径，例如相册目录结构）。</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    [JsonPropertyName("size")]
    public ulong Size { get; set; }

    /// <summary>文件 MIME 类型，例如 <c>"image/png"</c>。</summary>
    [JsonPropertyName("fileType")]
    public string FileType { get; set; } = string.Empty;

    /// <summary>SHA-256 哈希（小写十六进制），可选。</summary>
    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; set; }

    /// <summary>预览图（通常是 base64 编码的缩略图），可选。</summary>
    [JsonPropertyName("preview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preview { get; set; }

    /// <summary>文件元数据，可选。</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileMetadata? Metadata { get; set; }
}
