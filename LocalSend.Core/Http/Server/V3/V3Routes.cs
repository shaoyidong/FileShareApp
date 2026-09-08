using System.Net;
using System.Text.Json;
using LocalSend.Core.Crypto;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.State;
using LocalSend.Core.Util;
using LruCache = LocalSend.Core.Util.LruCache<string, byte[]>;

namespace LocalSend.Core.Http.Server.V3;

/// <summary>
/// v3 协议的请求处理逻辑，对应 Rust 端 <c>src/http/server/v3.rs</c>。
/// </summary>
/// <remarks>
/// v3 主要在 v2 之上引入了 nonce 交换与签名指纹。
/// 这里只实现 <c>POST /api/localsend/v3/nonce</c> 与 <c>POST /api/localsend/v3/register</c>。
/// </remarks>
public sealed class V3Routes
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Func<ClientInfo> _getInfo;
    private readonly Func<bool> _hasWeb;

    /// <summary>从远端收到的 nonce（按设备标识索引）。</summary>
    public LruCache ReceivedNonceMap { get; } = new(200);

    /// <summary>本端生成、期望远端验证的 nonce。</summary>
    public LruCache GeneratedNonceMap { get; } = new(200);

    public SemaphoreSlim ReceivedLock { get; } = new(1, 1);
    public SemaphoreSlim GeneratedLock { get; } = new(1, 1);

    public V3Routes(Func<ClientInfo> getInfo, Func<bool> hasWeb)
    {
        _getInfo = getInfo;
        _hasWeb = hasWeb;
    }

    /// <summary>处理 <c>POST /api/localsend/v3/nonce</c>。</summary>
    public async Task<JsonResponse<NonceResponse>> NonceExchangeAsync(
        Stream body, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        var payload = await ReadJsonAsync<NonceRequest>(body, ct).ConfigureAwait(false);

        byte[] nonce;
        try
        {
            nonce = Base64Url.Decode(payload.Nonce);
        }
        catch
        {
            throw AppError.BadRequest("Invalid nonce format");
        }
        if (!Nonce.ValidateNonce(nonce))
        {
            throw AppError.BadRequest("Invalid nonce");
        }

        var remoteKey = clientInfo.Identifier();

        await ReceivedLock.WaitAsync(ct).ConfigureAwait(false);
        try { ReceivedNonceMap.Put(remoteKey, nonce); }
        finally { ReceivedLock.Release(); }

        // 为对端生成新的 nonce。
        var newNonce = Nonce.GenerateNonce();
        var newNonceBase64 = Base64Url.Encode(newNonce);
        await GeneratedLock.WaitAsync(ct).ConfigureAwait(false);
        try { GeneratedNonceMap.Put(remoteKey, newNonce); }
        finally { GeneratedLock.Release(); }

        return new JsonResponse<NonceResponse>(HttpStatusCode.OK, new NonceResponse { Nonce = newNonceBase64 });
    }

    /// <summary>处理 <c>POST /api/localsend/v3/register</c>。</summary>
    public async Task<JsonResponse<RegisterResponseDto>> RegisterAsync(
        Stream body, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        var payload = await ReadJsonAsync<RegisterDto>(body, ct).ConfigureAwait(false);
        var info = _getInfo();

        return new JsonResponse<RegisterResponseDto>(HttpStatusCode.OK, new RegisterResponseDto
        {
            Alias = info.Alias,
            Version = info.Version,
            DeviceModel = info.DeviceModel,
            DeviceType = info.DeviceType,
            Token = info.Token,
            HasWebInterface = _hasWeb()
        });
    }

    private static async Task<T> ReadJsonAsync<T>(Stream body, CancellationToken ct)
    {
        try
        {
            var result = await JsonSerializer.DeserializeAsync<T>(body, JsonOpts, ct).ConfigureAwait(false);
            return result ?? throw AppError.BadRequest("Invalid JSON body");
        }
        catch (JsonException)
        {
            throw AppError.BadRequest("Invalid JSON body");
        }
    }
}

/// <summary>RequestClientInfo 上的辅助扩展，对应 Rust 端的同名方法。</summary>
public static class RequestClientInfoExtensions
{
    /// <summary>
    /// 设备标识：TLS 下使用证书公钥 PEM，否则使用 IP 字符串。
    /// 对应 Rust 端 <c>RequestClientInfo::identifier</c>。
    /// </summary>
    public static string Identifier(this RequestClientInfo info)
    {
        if (info.Cert != null)
        {
            try
            {
                return CertVerifier.PublicKeyFromCertDer(info.Cert);
            }
            catch
            {
                // 提取公钥失败时退回到 IP。
            }
        }
        return info.Ip.ToString();
    }
}
