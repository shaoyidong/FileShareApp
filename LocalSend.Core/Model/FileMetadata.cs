using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSend.Core.Model;

/// <summary>
/// 文件元数据，对应 Rust 端 <c>src/model/transfer.rs</c> 中的 <c>FileMetadata</c>。
/// </summary>
/// <remarks>
/// 字段为可选，序列化时若为 <c>null</c> 则省略，对应 Rust 的
/// <c>#[serde(skip_serializing_if = "Option::is_none")]</c>。
/// </remarks>
public class FileMetadata
{
    /// <summary>最后修改时间（通常是 ISO 8601 字符串）。可为空。</summary>
    [JsonPropertyName("modified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Modified { get; set; }

    /// <summary>最后访问时间。可为空。</summary>
    [JsonPropertyName("accessed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accessed { get; set; }
}
