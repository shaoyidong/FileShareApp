using System.Net;
using System.Text.Json;
using LocalSend.Core.Http.Dto;

namespace LocalSend.Core.Http.Server.Common;

/// <summary>
/// 服务端处理请求时返回的错误，对应 Rust 端 <c>src/http/server/common/error.rs</c> 中的 <c>AppError</c>。
/// </summary>
public sealed class AppError : Exception
{
    public HttpStatusCode Status { get; }
    public string? Detail { get; }

    public AppError(HttpStatusCode status, string? detail = null)
        : base(detail ?? status.ToString())
    {
        Status = status;
        Detail = detail;
    }

    public static AppError BadRequest(string message) => new(HttpStatusCode.BadRequest, message);
    /// <summary>仅指定状态码，无具体消息。</summary>
    public static AppError WithStatus(HttpStatusCode status) => new(status);
    public static AppError WithMessage(HttpStatusCode status, string message) => new(status, message);

    /// <summary>
    /// 把错误转成 JSON 响应体，对应 Rust 端 <c>AppError::to_response</c>。
    /// </summary>
    public byte[] ToJsonResponseBytes()
    {
        var message = Detail ?? Status switch
        {
            HttpStatusCode.InternalServerError => "Internal server error",
            _ => $"Status code: {(int)Status}"
        };
        var body = new ErrorResponse { Message = message };
        return JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions.Default);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
