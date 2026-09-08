using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Dto;

/// <summary>
/// v3 协议的 nonce 请求，对应 Rust 端 <c>NonceRequest</c>。
/// </summary>
public class NonceRequest
{
    /// <summary>Base64-URL 编码的 nonce 字符串。</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>
/// v3 协议的 nonce 响应，对应 Rust 端 <c>NonceResponse</c>。
/// </summary>
public class NonceResponse
{
    /// <summary>Base64-URL 编码的 nonce 字符串。</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>
/// 错误响应，对应 Rust 端 <c>ErrorResponse</c>。
/// </summary>
public class ErrorResponse
{
    /// <summary>错误信息。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
