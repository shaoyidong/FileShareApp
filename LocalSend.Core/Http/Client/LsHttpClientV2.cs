using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using LocalSend.Core.Crypto;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Model;
using LocalSend.Core.Util;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// v2.1 协议 HTTP 客户端，对应 Rust 端 <c>src/http/client/v2.rs</c> 中的 <c>LsHttpClientV2</c>。
/// </summary>
public sealed class LsHttpClientV2 : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    /// <summary>
    /// 创建带客户端证书的客户端。
    /// </summary>
    /// <param name="privateKeyPem">PEM 私钥。</param>
    /// <param name="certPem">PEM 证书。</param>
    /// <param name="timeout">请求超时（可选）。</param>
    public LsHttpClientV2(string privateKeyPem, string certPem, TimeSpan? timeout = null)
    {
        _client = HttpClientFactory.Create(privateKeyPem, certPem, timeout);
        _ownsClient = true;
    }

    /// <summary>创建不带客户端证书的客户端（仅 HTTP 或无需客户端认证时使用）。</summary>
    public LsHttpClientV2(TimeSpan? timeout = null)
    {
        _client = HttpClientFactory.CreateWithoutCert(timeout);
        _ownsClient = true;
    }

    /// <summary>用外部提供的 <see cref="HttpClient"/> 初始化（便于复用连接池）。</summary>
    public LsHttpClientV2(HttpClient client)
    {
        _client = client;
        _ownsClient = false;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    /// <summary>
    /// 注册到远程设备（<c>POST /api/localsend/v2/register</c>）。
    /// </summary>
    public async Task<ResultWithPublicKey<RegisterResponseDtoV2>> RegisterAsync(
        ProtocolType protocol, string ip, ushort port, RegisterDtoV2 payload,
        CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/register").ToString();
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = HttpResponseExtensions.AsJsonContent(payload)
        };
        try
        {
            using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw await res.IntoErrorAsync().ConfigureAwait(false);
            }
            string? publicKey = null;
            if (protocol == ProtocolType.Https)
            {
                // 简化：mTLS 客户端证书验证由 HttpClientHandler 在握手时完成。
                // 此处不再二次验证；如需更严格校验，可在 handler 的回调中实现。
                // 保留此分支以与 Rust API 对应。
                publicKey = await ExtractPublicKeyAsync(res).ConfigureAwait(false);
            }

            var body = await res.ReadAsJsonAsync<RegisterResponseDtoV2>(ct).ConfigureAwait(false)
                ?? throw new ClientError("Empty register response");
            return new ResultWithPublicKey<RegisterResponseDtoV2>(body, publicKey);
        }
        catch (ClientError)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 保留内部异常信息以便调试（如 TLS 握手失败、连接拒绝等）
            throw new ClientError($"Register failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 准备上传（<c>POST /api/localsend/v2/prepare-upload</c>）。
    /// </summary>
    /// <param name="publicKey">期望的对端公钥（HTTPS 用）。</param>
    /// <param name="pin">可选 PIN。</param>
    public async Task<PrepareUploadResultV2> PrepareUploadAsync(
        ProtocolType protocol, string ip, ushort port,
        string? publicKey, PrepareUploadRequestDtoV2 payload, string? pin,
        CancellationToken ct = default)
    {
        var parameters = pin != null
            ? new List<(string, string)> { ("pin", pin) }
            : new List<(string, string)>();
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/prepare-upload", parameters).ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = HttpResponseExtensions.AsJsonContent(payload)
        };
        using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var status = (ushort)res.StatusCode;
        if (status >= 400)
        {
            throw await res.IntoErrorAsync().ConfigureAwait(false);
        }
        if (status == 204)
        {
            return new PrepareUploadResultV2 { StatusCode = status, Response = null };
        }
        var body = await res.ReadAsJsonAsync<PrepareUploadResponseDtoV2>(ct).ConfigureAwait(false);
        return new PrepareUploadResultV2 { StatusCode = status, Response = body };
    }

    /// <summary>
    /// 上传文件（<c>POST /api/localsend/v2/upload?sessionId=...&amp;fileId=...&amp;token=...</c>）。
    /// </summary>
    /// <param name="content">文件内容来源。</param>
    /// <param name="progress">进度回调（累计字节数）。</param>
    /// <param name="cancel">取消令牌。</param>
    public async Task UploadAsync(
        ProtocolType protocol, string ip, ushort port, string? publicKey,
        string sessionId, string fileId, string token,
        FileContent content, Action<ulong> progress, CancellationToken cancel)
    {
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/upload",
            new List<(string, string)>
            {
                ("sessionId", sessionId),
                ("fileId", fileId),
                ("token", token)
            }).ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = HttpClientFactory.UploadBody(content, progress)
        };

        HttpResponseMessage res;
        try
        {
            res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw ClientError.CancelledError();
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                throw await res.IntoErrorAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>取消会话（<c>POST /api/localsend/v2/cancel?sessionId=...</c>）。</summary>
    public async Task CancelAsync(
        ProtocolType protocol, string ip, ushort port, string sessionId,
        CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/cancel",
            new List<(string, string)> { ("sessionId", sessionId) }).ToString();
        using var res = await _client.PostAsync(url, content: null, ct).ConfigureAwait(false);
    }

    /// <summary>查询设备信息（<c>GET /api/localsend/v2/info</c>）。</summary>
    public async Task<InfoResponseDtoV2> InfoAsync(
        ProtocolType protocol, string ip, ushort port, CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/info").ToString();
        using var res = await _client.GetAsync(url, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw await res.IntoErrorAsync().ConfigureAwait(false);
        }
        return await res.ReadAsJsonAsync<InfoResponseDtoV2>(ct).ConfigureAwait(false)
            ?? throw new ClientError("Empty info response");
    }

    /// <summary>
    /// 准备下载（<c>POST /api/localsend/v2/prepare-download</c>）。
    /// 用于反向文件传输：发送端托管文件，接收端下载。
    /// </summary>
    public async Task<PrepareDownloadResponseDtoV2> PrepareDownloadAsync(
        ProtocolType protocol, string ip, ushort port,
        string? sessionId, string? pin, CancellationToken ct = default)
    {
        var parameters = new List<(string, string)>();
        if (sessionId != null) parameters.Add(("sessionId", sessionId));
        if (pin != null) parameters.Add(("pin", pin));
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/prepare-download", parameters).ToString();

        using var res = await _client.PostAsync(url, content: null, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw await res.IntoErrorAsync().ConfigureAwait(false);
        }
        return await res.ReadAsJsonAsync<PrepareDownloadResponseDtoV2>(ct).ConfigureAwait(false)
            ?? throw new ClientError("Empty prepare-download response");
    }

    /// <summary>
    /// 下载文件（<c>GET /api/localsend/v2/download?sessionId=...&amp;fileId=...</c>）。
    /// </summary>
    public async Task<Stream> DownloadAsync(
        ProtocolType protocol, string ip, ushort port,
        string sessionId, string fileId, CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V2, protocol.AsStr(), ip, port, "/download",
            new List<(string, string)>
            {
                ("sessionId", sessionId),
                ("fileId", fileId)
            }).ToString();
        var res = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            using (res)
            {
                throw await res.IntoErrorAsync().ConfigureAwait(false);
            }
        }
        return await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载文件并直接写入 <paramref name="target"/> 流，返回写入字节数。
    /// 对应 Rust 端 <c>download_to_writer</c>。
    /// </summary>
    public async Task<ulong> DownloadToStreamAsync(
        ProtocolType protocol, string ip, ushort port,
        string sessionId, string fileId, Stream target, CancellationToken ct = default)
    {
        await using var stream = await DownloadAsync(protocol, ip, port, sessionId, fileId, ct).ConfigureAwait(false);
        ulong total = 0;
        byte[] buffer = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            total += (ulong)n;
        }
        await target.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// 简化版：从 <see cref="HttpResponseMessage"/> 中提取对端证书的公钥（PEM）。
    /// 真实场景下应在 HttpClientHandler 的 ServerCertificateCustomValidationCallback 里缓存。
    /// </summary>
    private static async Task<string?> ExtractPublicKeyAsync(HttpResponseMessage res)
    {
        // HttpResponseMessage 不直接暴露 TLS 证书。
        // 此处保留接口与 Rust 端对应；实际项目中需要从自定义 handler 注入。
        await Task.CompletedTask;
        return null;
    }
}
