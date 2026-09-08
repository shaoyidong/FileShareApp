using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Dto;

/// <summary>
/// v3 协议的 prepare-upload 请求，对应 Rust 端 <c>PrepareUploadRequestDto</c>。
/// 用于 <c>POST /api/localsend/v3/prepare-upload</c>。
/// </summary>
public class PrepareUploadRequestDto
{
    /// <summary>发送端设备信息。</summary>
    [JsonPropertyName("info")]
    public RegisterDto Info { get; set; } = new();

    /// <summary>待上传文件，键为 file ID，值为文件元数据。</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, FileDto> Files { get; set; } = new();
}

/// <summary>
/// v3 协议的 prepare-upload 响应，对应 Rust 端 <c>PrepareUploadResponseDto</c>。
/// </summary>
public class PrepareUploadResponseDto
{
    /// <summary>会话 ID（接收端分配）。</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 被接收端接受的文件，键为 file ID，值为该文件的上传 token。
    /// </summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new();
}

/// <summary>
/// prepare-upload 的完整结果（含 HTTP 状态码），对应 Rust 端 <c>PrepareUploadResult</c>。
/// </summary>
public class PrepareUploadResult
{
    /// <summary>HTTP 状态码（200 表示成功；204 表示无需传输）。</summary>
    public ushort StatusCode { get; set; }

    /// <summary>
    /// 响应体；204 / 4xx / 5xx 时为 <c>null</c>。
    /// </summary>
    public PrepareUploadResponseDto? Response { get; set; }
}
