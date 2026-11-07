using System.Net;

namespace LocalSend.Core.Http.Server.V2;

/// <summary>轻量的 HTTP 响应抽象，便于在不同的 HTTP 后端之间复用。</summary>
public interface IHttpResponse
{
    HttpStatusCode Status { get; }
    string? ContentType { get; }
    byte[]? Body { get; }
    /// <summary>额外的响应头（key, value）。</summary>
    IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; }
}

/// <summary>
/// JSON 响应，对应 Rust 端 <c>JsonResponse&lt;T&gt;</c>。
/// <para>
/// AOT 下不允许在序列化时按泛型反射推导类型，因此构造函数直接接收
/// 已经用源生成元数据序列化好的 body（调用点用 <c>LsJson.SerializeToUtf8Bytes</c>）。
/// </para>
/// </summary>
public sealed class JsonResponse : IHttpResponse
{
    public HttpStatusCode Status { get; }
    public string? ContentType => "application/json";
    public byte[]? Body { get; }
    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = Array.Empty<(string, string)>();

    public JsonResponse(HttpStatusCode status, byte[] body)
    {
        Status = status;
        Body = body;
    }
}

/// <summary>空响应体。</summary>
public sealed class EmptyResponse : IHttpResponse
{
    public HttpStatusCode Status { get; }
    public string? ContentType => null;
    public byte[]? Body => null;
    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = Array.Empty<(string, string)>();
    public EmptyResponse(HttpStatusCode status) { Status = status; }
}
