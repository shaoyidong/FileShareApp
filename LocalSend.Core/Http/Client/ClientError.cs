using System.Net.Http;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// 客户端错误，对应 Rust 端 <c>src/http/client/mod.rs</c> 中的 <c>ClientError</c>。
/// </summary>
public class ClientError : Exception
{
    public ClientError(string message) : base(message) { }
    public ClientError(string message, Exception inner) : base(message, inner) { }

    /// <summary>状态码错误（HTTP 4xx / 5xx）。</summary>
    public StatusCodeError? StatusCodeError { get; init; }

    /// <summary>上传被取消。</summary>
    public bool Cancelled { get; init; }

    public static ClientError FromStatusCode(StatusCodeError err) =>
        new(err.MessageBody ?? $"HTTP {err.Status}", err) { StatusCodeError = err };

    public static ClientError FromReqwest(string message, Exception inner) =>
        new(message, inner);

    public static ClientError CancelledError() =>
        new("Upload cancelled") { Cancelled = true };
}
