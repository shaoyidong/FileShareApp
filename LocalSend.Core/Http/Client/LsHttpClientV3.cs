using System.Net.Http;
using System.Threading.Channels;
using LocalSend.Core.Crypto;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Model;
using LocalSend.Core.Util;
using LruCache = LocalSend.Core.Util.LruCache<string, byte[]>;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// v3 协议 HTTP 客户端，对应 Rust 端 <c>src/http/client/v3.rs</c> 中的 <c>LsHttpClientV3</c>。
/// </summary>
/// <remarks>
/// 相比 v2，v3 多了一步 nonce 交换，并使用 <c>ed25519</c> 签名指纹替代随机字符串指纹。
/// 这里维护两份 LRU 缓存：
/// <list type="bullet">
///   <item><see cref="_receivedNonceMap"/>：从远端收到的 nonce（按设备标识索引）。</item>
///   <item><see cref="_generatedNonceMap"/>：本端生成、期望从远端验证的 nonce。</item>
/// </list>
/// </remarks>
public sealed class LsHttpClientV3 : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _receivedLock = new(1, 1);
    private readonly SemaphoreSlim _generatedLock = new(1, 1);
    private readonly LruCache _receivedNonceMap = new(200);
    private readonly LruCache _generatedNonceMap = new(200);

    public LsHttpClientV3(string privateKeyPem, string certPem, TimeSpan? timeout = null)
    {
        _client = HttpClientFactory.Create(privateKeyPem, certPem, timeout);
        _ownsClient = true;
    }

    public LsHttpClientV3(HttpClient client)
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
        _receivedLock.Dispose();
        _generatedLock.Dispose();
    }

    /// <summary>
    /// 与远端交换 nonce（<c>POST /api/localsend/v3/nonce</c>）。
    /// 同时把本端生成的 nonce 与远端响应的 nonce 都记入缓存。
    /// </summary>
    public async Task<string> NonceAsync(
        ProtocolType protocol, string ip, ushort port, CancellationToken ct = default)
    {
        var generatedNonce = Nonce.GenerateNonce();
        var generatedNonceBase64 = Base64Url.Encode(generatedNonce);

        var requestBody = new NonceRequest { Nonce = generatedNonceBase64 };
        var url = new TargetUrl(ApiVersion.V3, protocol.AsStr(), ip, port, "/nonce").ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = HttpResponseExtensions.AsJsonContent(requestBody)
        };
        using var res = await _client.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw await res.IntoErrorAsync().ConfigureAwait(false);
        }

        // 远端标识：HTTPS 下用公钥，HTTP 下用 IP。
        var remoteKey = await ToIdentifierAsync(res, protocol == ProtocolType.Https).ConfigureAwait(false);
        var body = await res.ReadAsJsonAsync<NonceResponse>(ct).ConfigureAwait(false)
            ?? throw new ClientError("Empty nonce response");
        var responseNonce = Base64Url.Decode(body.Nonce);

        await _receivedLock.WaitAsync(ct).ConfigureAwait(false);
        try { _receivedNonceMap.Put(remoteKey, responseNonce); }
        finally { _receivedLock.Release(); }

        await _generatedLock.WaitAsync(ct).ConfigureAwait(false);
        try { _generatedNonceMap.Put(remoteKey, generatedNonce); }
        finally { _generatedLock.Release(); }

        return body.Nonce;
    }

    /// <summary>
    /// 注册（<c>POST /api/localsend/v3/register</c>）。
    /// </summary>
    public async Task<ResultWithPublicKey<RegisterResponseDto>> RegisterAsync(
        ProtocolType protocol, string ip, ushort port, RegisterDto payload,
        CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V3, protocol.AsStr(), ip, port, "/register").ToString();
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = HttpResponseExtensions.AsJsonContent(payload)
        };
        using var res = await _client.SendAsync(req, ct).ConfigureAwait(false);

        string? publicKey = null;
        if (protocol == ProtocolType.Https)
        {
            publicKey = await ExtractPublicKeyAsync(res).ConfigureAwait(false);
        }

        var body = await res.ReadAsJsonAsync<RegisterResponseDto>(ct).ConfigureAwait(false)
            ?? throw new ClientError("Empty register response");
        return new ResultWithPublicKey<RegisterResponseDto>(body, publicKey);
    }

    /// <summary>准备上传（<c>POST /api/localsend/v3/prepare-upload</c>）。</summary>
    public async Task<PrepareUploadResult> PrepareUploadAsync(
        ProtocolType protocol, string ip, ushort port,
        string? publicKey, PrepareUploadRequestDto payload,
        CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V3, protocol.AsStr(), ip, port, "/prepare-upload").ToString();
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
            return new PrepareUploadResult { StatusCode = status, Response = null };
        }
        var body = await res.ReadAsJsonAsync<PrepareUploadResponseDto>(ct).ConfigureAwait(false);
        return new PrepareUploadResult { StatusCode = status, Response = body };
    }

    /// <summary>上传文件（<c>POST /api/localsend/v3/upload</c>）。</summary>
    public async Task UploadAsync(
        ProtocolType protocol, string ip, ushort port, string? publicKey,
        string sessionId, string fileId, string token,
        FileContent content, Action<ulong> progress, CancellationToken cancel)
    {
        var url = new TargetUrl(ApiVersion.V3, protocol.AsStr(), ip, port, "/upload",
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

    /// <summary>取消会话（<c>POST /api/localsend/v3/cancel?sessionId=...</c>）。</summary>
    public async Task CancelAsync(
        ProtocolType protocol, string ip, ushort port, string sessionId,
        CancellationToken ct = default)
    {
        var url = new TargetUrl(ApiVersion.V3, protocol.AsStr(), ip, port, "/cancel",
            new List<(string, string)> { ("sessionId", sessionId) }).ToString();
        using var res = await _client.PostAsync(url, content: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 计算远端标识。HTTPS 下提取证书公钥；HTTP 下退回到远端 IP。
    /// 对应 Rust 端 <c>to_identifier</c>。
    /// </summary>
    private static async Task<string> ToIdentifierAsync(HttpResponseMessage res, bool requireCert)
    {
        await Task.CompletedTask;
        if (requireCert)
        {
            // 见 LsHttpClientV2 中说明：实际项目应从 handler 回调中拿到证书。
            // 此处简化为使用响应来源 IP 占位。
        }
        // HttpResponseMessage 不直接暴露远端 IP，这里用 res.RequestMessage 的 Host。
        return res.RequestMessage?.RequestUri?.Host ?? "unknown";
    }

    private static Task<string?> ExtractPublicKeyAsync(HttpResponseMessage res)
    {
        // 同 LsHttpClientV2.ExtractPublicKeyAsync，简化处理。
        return Task.FromResult<string?>(null);
    }
}
