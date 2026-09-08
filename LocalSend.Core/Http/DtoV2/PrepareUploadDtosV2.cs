using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.DtoV2;

/// <summary>
/// v2.1 协议的 prepare-upload 请求，对应 Rust 端 <c>PrepareUploadRequestDtoV2</c>。
/// 用于 <c>POST /api/localsend/v2/prepare-upload</c>。
/// </summary>
public class PrepareUploadRequestDtoV2
{
    /// <summary>发送端设备信息。</summary>
    [JsonPropertyName("info")]
    public RegisterDtoV2 Info { get; set; } = new();

    /// <summary>待上传文件，键为 file ID。</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, FileDto> Files { get; set; } = new();
}

/// <summary>
/// v2.1 协议的 prepare-upload 响应，对应 Rust 端 <c>PrepareUploadResponseDtoV2</c>。
/// </summary>
public class PrepareUploadResponseDtoV2
{
    /// <summary>会话 ID。</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>被接收端接受的文件，键为 file ID，值为上传 token。</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new();
}

/// <summary>
/// prepare-upload 的完整结果（含 HTTP 状态码），对应 Rust 端 <c>PrepareUploadResultV2</c>。
/// </summary>
public class PrepareUploadResultV2
{
    public ushort StatusCode { get; set; }
    public PrepareUploadResponseDtoV2? Response { get; set; }
}
