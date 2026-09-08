namespace LocalSend.Core.Http;

/// <summary>
/// HTTP 状态码错误，对应 Rust 端 <c>src/http/mod.rs</c> 中的 <c>StatusCodeError</c>。
/// </summary>
public class StatusCodeError : Exception
{
    public StatusCodeError(ushort status, string? message)
        : base($"{status};{message}")
    {
        Status = status;
        MessageBody = message;
    }

    /// <summary>HTTP 状态码。</summary>
    public ushort Status { get; }

    /// <summary>响应体中的错误信息（可能为空）。</summary>
    public string? MessageBody { get; }
}
