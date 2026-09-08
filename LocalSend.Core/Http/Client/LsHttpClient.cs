using System.Net.Http;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// HTTP 客户端统一入口，对应 Rust 端 <c>src/http/client/mod.rs</c> 中的 <c>LsHttpClient</c> 枚举。
/// </summary>
/// <remarks>
/// 根据 <see cref="LsHttpClientVersion"/> 把调用分发到 <see cref="LsHttpClientV2"/> 或 <see cref="LsHttpClientV3"/>。
/// 调用方只需要面向 v3 DTO 编程，由本类负责 v3 ↔ v2 之间的 DTO 转换。
/// </remarks>
public sealed class LsHttpClient : IDisposable
{
    private readonly LsHttpClientVersion _version;
    private readonly LsHttpClientV2 _v2;
    private readonly LsHttpClientV3 _v3;

    public LsHttpClient(string privateKeyPem, string certPem, LsHttpClientVersion version, TimeSpan? timeout = null)
    {
        _version = version;
        if (version == LsHttpClientVersion.V2)
        {
            _v2 = new LsHttpClientV2(privateKeyPem, certPem, timeout);
            _v3 = null!;
        }
        else
        {
            _v2 = null!;
            _v3 = new LsHttpClientV3(privateKeyPem, certPem, timeout);
        }
    }

    public void Dispose()
    {
        if (_version == LsHttpClientVersion.V2) _v2.Dispose();
        else _v3.Dispose();
    }

    public async Task<ResultWithPublicKey<RegisterResponseDto>> RegisterAsync(
        ProtocolType protocol, string ip, ushort port, RegisterDto payload,
        CancellationToken ct = default)
    {
        if (_version == LsHttpClientVersion.V2)
        {
            var result = await _v2.RegisterAsync(protocol, ip, port, payload.ToV2(), ct).ConfigureAwait(false);
            return new ResultWithPublicKey<RegisterResponseDto>(result.Body.ToV3(), result.PublicKey);
        }
        return await _v3.RegisterAsync(protocol, ip, port, payload, ct).ConfigureAwait(false);
    }

    public async Task<PrepareUploadResult> PrepareUploadAsync(
        ProtocolType protocol, string ip, ushort port,
        string? publicKey, PrepareUploadRequestDto payload, string? pin,
        CancellationToken ct = default)
    {
        if (_version == LsHttpClientVersion.V2)
        {
            var result = await _v2.PrepareUploadAsync(protocol, ip, port, publicKey, payload.ToV2(), pin, ct).ConfigureAwait(false);
            return result.ToV3();
        }
        return await _v3.PrepareUploadAsync(protocol, ip, port, publicKey, payload, ct).ConfigureAwait(false);
    }

    public Task UploadAsync(
        ProtocolType protocol, string ip, ushort port, string? publicKey,
        string sessionId, string fileId, string token,
        FileContent content, Action<ulong> progress, CancellationToken cancel)
    {
        if (_version == LsHttpClientVersion.V2)
        {
            return _v2.UploadAsync(protocol, ip, port, publicKey, sessionId, fileId, token, content, progress, cancel);
        }
        return _v3.UploadAsync(protocol, ip, port, publicKey, sessionId, fileId, token, content, progress, cancel);
    }

    public Task CancelAsync(
        ProtocolType protocol, string ip, ushort port, string sessionId,
        CancellationToken ct = default)
    {
        if (_version == LsHttpClientVersion.V2)
        {
            return _v2.CancelAsync(protocol, ip, port, sessionId, ct);
        }
        return _v3.CancelAsync(protocol, ip, port, sessionId, ct);
    }
}

public enum LsHttpClientVersion
{
    V2,
    V3
}
