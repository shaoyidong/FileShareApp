using System.Net.Http.Json;
using System.Text.Json;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// HTTP 响应扩展方法，对应 Rust 端 <c>ResponseExt::into_error</c>。
/// </summary>
internal static class HttpResponseExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 当响应不成功时，尝试解析错误体为 <see cref="ErrorResponse"/>，
    /// 失败则使用原始文本作为错误消息。
    /// </summary>
    public static async Task<ClientError> IntoErrorAsync(this HttpResponseMessage res)
    {
        var status = (ushort)res.StatusCode;
        var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        string? message = null;
        try
        {
            var err = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
            message = err?.Message;
        }
        catch
        {
            message = body;
        }
        return ClientError.FromStatusCode(new StatusCodeError(status, string.IsNullOrEmpty(message) ? null : message));
    }

    public static Task<T?> ReadAsJsonAsync<T>(this HttpResponseMessage res, CancellationToken ct = default)
    {
        return res.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    public static StringContent AsJsonContent<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }
}
