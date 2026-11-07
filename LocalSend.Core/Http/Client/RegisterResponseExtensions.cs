using System.Net.Http.Json;
using System.Text.Json;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Json;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// HTTP 响应扩展方法，对应 Rust 端 <c>ResponseExt::into_error</c>。
/// <para>所有 JSON 读写都经 <see cref="LsJson"/> 使用源生成元数据，AOT 下无反射。</para>
/// </summary>
internal static class HttpResponseExtensions
{
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
            var err = LsJson.Deserialize<ErrorResponse>(body);
            message = err?.Message;
        }
        catch (JsonException)
        {
            message = body;
        }
        return ClientError.FromStatusCode(new StatusCodeError(status, string.IsNullOrEmpty(message) ? null : message));
    }

    public static Task<T?> ReadAsJsonAsync<T>(this HttpResponseMessage res, CancellationToken ct = default)
    {
        return res.Content.ReadFromJsonAsync(LsJson.Info<T>(), ct);
    }

    public static StringContent AsJsonContent<T>(T payload)
    {
        var json = LsJson.Serialize(payload);
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }
}
