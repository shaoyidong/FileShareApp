using FileShare.Core.Models;
using LocalSend.Core.Crypto;
using LocalSend.Core.Http.Client;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services;

/// <summary>
/// 通过 LocalSend 协议发现的设备（第三方客户端）。
/// </summary>
public sealed record LocalSendPeer(
    string DeviceId,
    string Alias,
    string IpAddress,
    ushort Port,
    ProtocolTypeV2 Protocol,
    string Fingerprint,
    LocalSend.Core.Model.DeviceType DeviceType = LocalSend.Core.Model.DeviceType.Desktop);

/// <summary>发送结果。</summary>
public sealed record LocalSendSendResult(bool Success, string? Error);

/// <summary>
/// LocalSend v2 发送端：<c>prepare-upload</c> → <c>upload</c>。
/// <para>
/// HTTPS + mTLS：携带本机证书，握手回调里校验对端证书指纹；
/// 指纹必须与对端声明一致，并且按 <c>ip:port</c> 做 TOFU（首次记录，之后变化即拒绝）。
/// </para>
/// </summary>
public sealed class LocalSendSender
{
    private const string FileId = "1";
    private const string ProtocolVersion = "2.3";

    private readonly LocalSendIdentity _identity;
    private readonly LocalSendTrustStore _trustStore;
    private readonly ILogger<LocalSendSender>? _logger;
    private readonly string _alias;
    private readonly TimeSpan _timeout;

    public LocalSendSender(
        string alias,
        LocalSendIdentity identity,
        LocalSendTrustStore trustStore,
        ILogger<LocalSendSender>? logger = null,
        TimeSpan? timeout = null)
    {
        _alias = string.IsNullOrWhiteSpace(alias) ? "FileShare" : alias;
        _identity = identity;
        _trustStore = trustStore;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>发送单个文件到 LocalSend 设备。</summary>
    public async Task<LocalSendSendResult> SendAsync(
        LocalSendPeer peer,
        string filePath,
        string? relativePath = null,
        Action<ulong, ulong>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return new LocalSendSendResult(false, "文件不存在");
        }

        var fileName = string.IsNullOrEmpty(relativePath) ? info.Name : relativePath.Replace('\\', '/');
        var useHttps = peer.Protocol == ProtocolTypeV2.Https;
        var endpoint = $"{peer.IpAddress}:{peer.Port}";
        var protocol = useHttps ? ProtocolType.Https : ProtocolType.Http;

        using var pinned = useHttps ? PinnedHttpClient.Create(peer, endpoint, _identity, _trustStore, _timeout, _logger) : null;
        using var httpClient = pinned?.Client ?? CreatePlainClient();
        using var client = new LsHttpClientV2(httpClient);

        // HTTPS 先做一次 info 探活：把握手/指纹校验的错误暴露出来（否则会被 prepare-upload 的异常掩盖）
        if (useHttps)
        {
            try
            {
                await client.InfoAsync(protocol, peer.IpAddress, peer.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message = pinned?.Error ?? ex.Message;
                _logger?.LogWarning("LocalSend 证书校验失败: {Alias} ({Endpoint}) {Message}", peer.Alias, endpoint, message);
                return new LocalSendSendResult(false, message);
            }
        }

        var payload = new PrepareUploadRequestDtoV2
        {
            Info = new RegisterDtoV2
            {
                Alias = _alias,
                Version = ProtocolVersion,
                DeviceType = LocalSend.Core.Model.DeviceType.Desktop,
                Fingerprint = _identity.Fingerprint,
                Port = 0,
                Protocol = useHttps ? ProtocolTypeV2.Https : ProtocolTypeV2.Http,
                Download = false
            },
            Files = new Dictionary<string, FileDto>
            {
                [FileId] = new FileDto
                {
                    Id = FileId,
                    FileName = fileName,
                    Size = (ulong)info.Length,
                    FileType = GuessContentType(info.Extension)
                }
            }
        };

        try
        {
            var prepare = await client
                .PrepareUploadAsync(protocol, peer.IpAddress, peer.Port, null, payload, null, cancellationToken)
                .ConfigureAwait(false);

            if (prepare.Response is null)
            {
                return new LocalSendSendResult(false, "对方未接受文件（可能拒绝了请求）");
            }

            if (!prepare.Response.Files.TryGetValue(FileId, out var token))
            {
                return new LocalSendSendResult(false, "对方拒绝了该文件");
            }

            var total = (ulong)info.Length;
            await client
                .UploadAsync(
                    protocol,
                    peer.IpAddress,
                    peer.Port,
                    null,
                    prepare.Response.SessionId,
                    FileId,
                    token,
                    new FileContent.FilePath(filePath),
                    sent => progress?.Invoke(sent, total),
                    cancellationToken)
                .ConfigureAwait(false);

            _logger?.LogInformation(
                "LocalSend 发送完成: {File} → {Alias} ({Endpoint}, {Protocol})",
                info.Name, peer.Alias, endpoint, protocol);

            return new LocalSendSendResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new LocalSendSendResult(false, "已取消");
        }
        catch (ClientError ex)
        {
            _logger?.LogWarning(ex, "LocalSend 发送失败（协议错误）: {File} → {Alias}", filePath, peer.Alias);
            return new LocalSendSendResult(false, pinned?.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LocalSend 发送失败: {File} → {Alias}", filePath, peer.Alias);
            return new LocalSendSendResult(false, pinned?.Error ?? ex.Message);
        }
    }

    /// <summary>
    /// 探测目标是否是 LocalSend 设备：先试 HTTPS（mTLS + TOFU），失败再试 HTTP。
    /// </summary>
    public async Task<LocalSendPeer?> ProbeAsync(string ipAddress, int port, CancellationToken cancellationToken = default)
    {
        var httpsPeer = await ProbeCoreAsync(ipAddress, port, ProtocolTypeV2.Https, cancellationToken).ConfigureAwait(false);
        if (httpsPeer is not null)
        {
            return httpsPeer;
        }

        return await ProbeCoreAsync(ipAddress, port, ProtocolTypeV2.Http, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalSendPeer?> ProbeCoreAsync(
        string ipAddress,
        int port,
        ProtocolTypeV2 protocolType,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{ipAddress}:{port}";
        var useHttps = protocolType == ProtocolTypeV2.Https;
        var protocol = useHttps ? ProtocolType.Https : ProtocolType.Http;

        var placeholder = new LocalSendPeer("localsend:probe", ipAddress, ipAddress, (ushort)port, protocolType, string.Empty);
        using var pinned = useHttps ? PinnedHttpClient.Create(placeholder, endpoint, _identity, _trustStore, TimeSpan.FromSeconds(5), _logger) : null;
        if (useHttps && pinned is null)
        {
            return null;
        }

        using var http = pinned?.Client ?? CreatePlainClient();
        using var client = new LsHttpClientV2(http);

        try
        {
            var info = await client.InfoAsync(protocol, ipAddress, (ushort)port, cancellationToken).ConfigureAwait(false);

            var fingerprint = useHttps
                ? pinned!.ObservedFingerprint ?? string.Empty
                : info.Fingerprint;

            // HTTPS：对端在 JSON 里声明的指纹必须与证书指纹一致，否则拒绝（防伪造身份）
            if (useHttps
                && !string.IsNullOrEmpty(info.Fingerprint)
                && !string.Equals(info.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning(
                    "LocalSend 设备声明指纹与证书不一致，已拒绝: {Endpoint} 声明={Declared} 实际={Actual}",
                    endpoint, info.Fingerprint, fingerprint);
                return null;
            }

            if (string.IsNullOrEmpty(fingerprint))
            {
                fingerprint = endpoint;
            }

            return new LocalSendPeer(
                DeviceId: "localsend:" + fingerprint,
                Alias: string.IsNullOrWhiteSpace(info.Alias) ? ipAddress : info.Alias,
                IpAddress: ipAddress,
                Port: (ushort)port,
                Protocol: protocolType,
                Fingerprint: fingerprint,
                DeviceType: info.DeviceType ?? LocalSend.Core.Model.DeviceType.Desktop);
        }
        catch (Exception ex)
        {
            if (useHttps && pinned?.Error is { } error)
            {
                _logger?.LogWarning("LocalSend 证书校验失败: {Endpoint} {Message}", endpoint, error);
            }
            else
            {
                _logger?.LogDebug(ex, "LocalSend 探测失败: {Endpoint} {Protocol}", endpoint, protocolType);
            }

            return null;
        }
    }
    private HttpClient CreatePlainClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        return new HttpClient(handler) { Timeout = _timeout };
    }

    /// <summary>按扩展名猜一个 MIME（LocalSend 只用于对端展示，不校验）。</summary>
    private static string GuessContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// 带客户端证书 + 指纹校验（TOFU）的 HttpClient。
    /// </summary>
    private sealed class PinnedHttpClient : IDisposable
    {
        private string? _error;

        private PinnedHttpClient()
        {
        }

        public HttpClient Client { get; private set; } = null!;

        /// <summary>握手时实际看到的对端证书指纹。</summary>
        public string? ObservedFingerprint { get; private set; }

        /// <summary>握手阶段的校验失败原因（成功时为 null）。</summary>
        public string? Error => _error;

        public static PinnedHttpClient? Create(
            LocalSendPeer peer,
            string endpoint,
            LocalSendIdentity identity,
            LocalSendTrustStore trustStore,
            TimeSpan timeout,
            ILogger? logger)
        {
            X509Certificate2 certificate;
            try
            {
                certificate = TlsCertificateLoader.LoadWithPrivateKey(identity.CertPem, identity.PrivateKeyPem);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "本机 LocalSend 证书无效");
                return null;
            }

            var holder = new PinnedHttpClient();
            var expected = peer.Fingerprint;

            var handler = new HttpClientHandler
            {
                ClientCertificates = { certificate }
            };

            handler.ServerCertificateCustomValidationCallback = (_, remoteCert, _, _) =>
            {
                if (remoteCert is null)
                {
                    holder._error = "对端未提供证书";
                    return false;
                }

                var fingerprint = Convert.ToHexString(SHA256.HashData(remoteCert.GetRawCertData()));
                holder.ObservedFingerprint = fingerprint;

                if (!string.IsNullOrEmpty(expected)
                    && !string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    holder._error = $"对端证书指纹与其声明的指纹不一致（声明 {expected}，实际 {fingerprint}）";
                    return false;
                }

                var (trusted, firstSeen) = trustStore.Verify(endpoint, fingerprint);
                if (!trusted)
                {
                    holder._error = $"对端证书指纹已变化（{endpoint}），可能被中间人替换；确认对方重装过系统后，可在设置里清除 LocalSend 信任记录";
                    return false;
                }

                if (firstSeen)
                {
                    logger?.LogInformation("首次信任 LocalSend 设备证书: {Endpoint} {Fingerprint}", endpoint, fingerprint);
                }

                return true;
            };

            holder.Client = new HttpClient(handler) { Timeout = timeout };
            return holder;
        }

        public void Dispose() => Client.Dispose();
    }
}