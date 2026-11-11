using FileShare.Core.Models;
using FileShare.Crypto;
using LocalSend.Core;
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
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileShare.LocalSend;

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

    /// <summary>文本消息正文已在 prepare-upload 送达后，等待 upload 确认的最长时间。</summary>
    private static readonly TimeSpan TextMessageUploadGrace = TimeSpan.FromSeconds(5);

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
    public Task<LocalSendSendResult> SendAsync(
        LocalSendPeer peer,
        string filePath,
        string? relativePath = null,
        Action<ulong, ulong>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return Task.FromResult(new LocalSendSendResult(false, "文件不存在"));
        }

        var fileName = string.IsNullOrEmpty(relativePath) ? info.Name : relativePath.Replace('\\', '/');
        return SendCoreAsync(
            peer, fileName, (ulong)info.Length, GuessContentType(info.Extension), preview: null,
            new FileContent.FilePath(filePath), progress, info.Name, cancellationToken);
    }

    /// <summary>
    /// 发送一条「文本消息」（Preview 信号方案，零协议改动）：
    /// FileDto 用 <c>text/plain</c> + 非空 <c>Preview</c> 承载正文，其余完全复用
    /// 现有 prepare-upload + upload 管线，因此与官方客户端互通无 400 风险。
    /// <para>
    /// 官方客户端断点实证：发文本消息时 Preview 承载正文、发文本文件时 Preview 为 null；
    /// 图片的 Preview 非空但 FileType 不是 <c>text/*</c>，所以判定规则必须同时看两者。
    /// </para>
    /// </summary>
    public Task<LocalSendSendResult> SendMessageAsync(
        LocalSendPeer peer,
        string text,
        CancellationToken cancellationToken = default)
    {
        var message = text ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(message);
        var fileName = Guid.NewGuid().ToString("N") + ".txt"; // 模仿官方文本消息命名

        // 直接把 UTF-8 字节推入通道，不产生任何临时文件
        var channel = Channel.CreateBounded<byte[]>(1);
        channel.Writer.TryWrite(bytes);
        channel.Writer.TryComplete();

        return SendCoreAsync(
            peer, fileName, (ulong)bytes.Length, "text/plain", message,
            new FileContent.FromStream(channel), progress: null, fileName, cancellationToken,
            textMessage: true);
    }

    /// <summary>prepare-upload → upload 的公共实现（文件与文本消息共用）。</summary>
    /// <param name="textMessage">
    /// 文本消息：正文已在 prepare-upload 的 Preview 字段送达，对端（官方客户端）通常先弹消息、
    /// 可能延迟甚至不返回 upload 的 HTTP 响应。因此上传完成后只给一个短宽限期，
    /// 数据完整写出即判「已送达」，避免白等 30 秒 HttpClient 超时把成功误判为失败。
    /// </param>
    private async Task<LocalSendSendResult> SendCoreAsync(
        LocalSendPeer peer,
        string fileName,
        ulong size,
        string fileType,
        string? preview,
        FileContent content,
        Action<ulong, ulong>? progress,
        string logName,
        CancellationToken cancellationToken,
        bool textMessage = false)
    {
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
                DeviceType = global::LocalSend.Core.Model.DeviceType.Desktop,
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
                    Size = size,
                    FileType = fileType,
                    Preview = preview
                }
            }
        };

        try
        {
            var prepare = await client
                .PrepareUploadAsync(protocol, peer.IpAddress, peer.Port, null, payload, null, cancellationToken)
                .ConfigureAwait(false);

            // ===== 文本消息：与「文件发送请求」彻底分流 =====
            // 正文随 prepare-upload 的 Preview 字段一起送达，对端（官方客户端）收到后
            // 弹窗展示；用户在弹窗上点「关闭」或「复制」只是本地交互，协议上可能表现为
            // 204 空响应 / files 为空 / 不提供 upload token。这些都代表「消息已收到」，
            // 绝不能沿用文件请求的「对方是否接受了这个文件」判定，也不能把
            // 「对方未接受文件（可能拒绝了请求）」这类文件语义的原因写进历史。
            if (textMessage)
            {
                return await FinishTextMessageAsync(
                    client, prepare, protocol, endpoint, peer, content, progress, size, cancellationToken)
                    .ConfigureAwait(false);
            }

            // ===== 以下为文件发送的既有判定，保持原样 =====
            if (prepare.Response is null)
            {
                return new LocalSendSendResult(false, "对方未接受文件（可能拒绝了请求）");
            }

            if (!prepare.Response.Files.TryGetValue(FileId, out var token))
            {
                return new LocalSendSendResult(false, "对方拒绝了该文件");
            }

            await client
                .UploadAsync(
                    protocol,
                    peer.IpAddress,
                    peer.Port,
                    null,
                    prepare.Response.SessionId,
                    FileId,
                    token,
                    content,
                    sent => progress?.Invoke(sent, size),
                    cancellationToken)
                .ConfigureAwait(false);

            _logger?.LogInformation(
                "LocalSend 发送完成: {File} → {Alias} ({Endpoint}, {Protocol})",
                logName, peer.Alias, endpoint, protocol);

            return new LocalSendSendResult(true, null);
        }
        catch (OperationCanceledException) when (textMessage && cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("LocalSend 文本消息发送已取消: → {Alias} ({Endpoint})", peer.Alias, endpoint);
            return new LocalSendSendResult(false, "已取消");
        }
        catch (OperationCanceledException) when (textMessage)
        {
            // prepare-upload 已发出但等响应超时：Preview 正文很可能已送达对端
            // （官方客户端常先弹消息再回 HTTP 响应）。文本消息按已送达处理，
            // 不套用文件请求的「是否被接受」失败判定。
            _logger?.LogWarning("LocalSend 文本消息响应超时，按已送达处理: → {Alias} ({Endpoint})", peer.Alias, endpoint);
            return new LocalSendSendResult(true, null);
        }
        catch (OperationCanceledException)
        {
            // 静默返回会让「数据实际已送达对端但客户端判定失败」的场景无任何痕迹，
            // 必须留下日志（历史记录由调用方按失败/取消补写）
            _logger?.LogInformation("LocalSend 发送已取消或超时: {File} → {Alias} ({Endpoint})", logName, peer.Alias, endpoint);
            return new LocalSendSendResult(false, "已取消");
        }
        catch (ClientError ex) when (textMessage && !cancellationToken.IsCancellationRequested)
        {
            // 文本消息：对端返回 4xx（例如点了「关闭」/ 拒绝附带文件）时，
            // 携带 Preview 正文的 prepare-upload 请求已经送达对端，消息已展示。
            // 不套用文件请求的「对方拒绝了文件」判定，只记日志便于排查。
            _logger?.LogWarning(
                "LocalSend 文本消息已送达，对端对附带文件返回协议错误（不影响消息）: {Alias} ({Endpoint}) {Reason}",
                peer.Alias, endpoint, ex.Message);
            return new LocalSendSendResult(true, null);
        }
        catch (ClientError ex)
        {
            _logger?.LogWarning(ex, "LocalSend 发送失败（协议错误）: {File} → {Alias}", logName, peer.Alias);
            return new LocalSendSendResult(false, pinned?.Error ?? ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LocalSend 发送失败: {File} → {Alias}", logName, peer.Alias);
            return new LocalSendSendResult(false, pinned?.Error ?? ex.Message);
        }
    }

    /// <summary>
    /// 文本消息专用终态：<b>不看文件是否被接受</b>。
    /// <para>
    /// 正文在 prepare-upload 的 Preview 字段里随请求一起送达，对端（官方客户端）
    /// 收到后就弹窗展示。用户在弹窗上点「关闭」或「复制」只是本地交互，
    /// 协议上表现为 204 空响应、files 为空、或不给 upload token —— 全部代表
    /// 「消息已收到」，必须记成成功，且不能出现任何文件语义的失败原因。
    /// 只有本方主动取消才是失败。
    /// </para>
    /// <para>
    /// 若对端确实给了 token（部分版本会要求把附带的 .txt 字节传完），
    /// 仍尽力上传，但只给短宽限；upload 超时 / 断连都不影响送达结论。
    /// </para>
    /// </summary>
    private async Task<LocalSendSendResult> FinishTextMessageAsync(
        LsHttpClientV2 client,
        PrepareUploadResultV2 prepare,
        ProtocolType protocol,
        string endpoint,
        LocalSendPeer peer,
        FileContent content,
        Action<ulong, ulong>? progress,
        ulong size,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("LocalSend 文本消息发送已取消: → {Alias} ({Endpoint})", peer.Alias, endpoint);
            return new LocalSendSendResult(false, "已取消");
        }

        // 有 token 才补发正文附带的字节；没有 token 说明对端只需要 Preview，直接完成。
        if (prepare.Response is not null &&
            prepare.Response.Files.TryGetValue(FileId, out var token))
        {
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadCts.CancelAfter(TextMessageUploadGrace);
            try
            {
                await client.UploadAsync(
                    protocol, peer.IpAddress, peer.Port, null,
                    prepare.Response.SessionId, FileId, token, content,
                    sent => progress?.Invoke(sent, size), uploadCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // upload 只是协议附带动作，正文此前已送达；不因它超时/被拒而改变结论
                _logger?.LogDebug(
                    "LocalSend 文本消息附带的 upload 未确认（不影响送达）: {Alias} ({Endpoint}) {Reason}",
                    peer.Alias, endpoint, ex.Message);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("LocalSend 文本消息发送已取消: → {Alias} ({Endpoint})", peer.Alias, endpoint);
            return new LocalSendSendResult(false, "已取消");
        }

        _logger?.LogInformation(
            "LocalSend 文本消息已送达: → {Alias} ({Endpoint}, {Protocol})",
            peer.Alias, endpoint, protocol);
        return new LocalSendSendResult(true, null);
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
                DeviceType: info.DeviceType ?? global::LocalSend.Core.Model.DeviceType.Desktop);
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
                // 统一走 FileShare.Crypto 的共享加载器（与 FileShare 自身 TLS 同一条路径）
                certificate = TlsCertificateLoader.LoadWithPrivateKey(
                    identity.CertPem, identity.PrivateKeyPem, trace: LsLog.Trace);
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