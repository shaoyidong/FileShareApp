using FileShare.Core.Common;
using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using FileShare.Crypto;
using LocalSend.Core;
using LocalSend.Core.Discovery;
using LocalSend.Core.Http.Client;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.State;
using LocalSend.Core.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.LocalSend;

// 接口定义已拆到 ILocalSendInteropHost.cs。

/// <inheritdoc cref="ILocalSendInteropHost"/>
public sealed class LocalSendInteropHost : ILocalSendInteropHost
{
    private const int DefaultPort = 53317;
    private const string ProtocolVersion = "2.3";

    private readonly IAppSettingsService _settingsService;
    private readonly IPlatformDirectoryService _directoryService;
    private readonly IDatabaseService _databaseService;
    private readonly ILocalSendUiBridge? _uiBridge;
    private readonly ILogger<LocalSendInteropHost> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly ConcurrentDictionary<string, LocalSendPeer> _peers = new();
    private readonly ConcurrentDictionary<string, PeerSessionInfo> _sessions = new();

    /// <summary>
    /// 文本消息条目的 transferId 集合。prepare-upload 阶段就已展示并入库，
    /// 随后的字节上传只做握手（目标指向临时文件，完成后立即删除，不落盘）。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _messageTransfers = new();

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private LsHttpClientV2? _discoveryClient;
    private LocalSendSender? _sender;
    private LocalSendIdentity? _identity;
    private LocalSendTrustStore? _trustStore;
    private LocalSendServer? _server;
    private MulticastDiscoveryService? _multicast;
    private CancellationTokenSource? _cts;
    private Task? _eventLoop;

    /// <summary>
    /// 上次成功应用的相关配置签名。<see cref="Apply"/> 会被启动流程和「保存设置」
    /// 反复调用；无条件 Stop+Start 会清空已发现的设备、让正在进行的组播监听与
    /// Dispose 竞态（日志里成串的 CancellationTokenSource has been disposed）。
    /// 只有真正影响本服务的配置变化才需要重启。
    /// </summary>
    private string? _appliedSignature;

    public LocalSendInteropHost(
        IAppSettingsService settingsService,
        IPlatformDirectoryService directoryService,
        IDatabaseService databaseService,
        ILoggerFactory loggerFactory,
        ILocalSendUiBridge? uiBridge = null)
    {
        _settingsService = settingsService;
        _directoryService = directoryService;
        _databaseService = databaseService;
        _uiBridge = uiBridge;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LocalSendInteropHost>();
    }

    /// <summary>上传会话的对端身份（用于传输列表与接收历史显示真实别名）。</summary>
    private sealed record PeerSessionInfo(string SenderId, string SenderName);

    /// <summary>上传会话内单个文件在 UI 传输列表中的稳定 ID。</summary>
    private static string TransferIdFor(string sessionId, string fileId) => $"ls-recv:{sessionId}:{fileId}";

    /// <summary>
    /// 观察一个后台任务的异常：这些任务生命周期与 InteropHost 一致，不能 await，
    /// 但直接 <c>_ = task</c> 会让异常变成 UnobservedTaskException 冲进全局日志，
    /// 且 Stop()/Dispose() 竞态产生的 ObjectDisposedException 会刷屏。
    /// </summary>
    private Task Observe(Task task, string what) =>
        task.ContinueWith(
            t =>
            {
                if (t.Exception is { } error && !IsExpectedShutdownError(error))
                {
                    _logger.LogWarning(error, "{What} 后台任务异常", what);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool IsExpectedShutdownError(AggregateException error) =>
        error.Flatten().InnerExceptions.All(ex =>
            ex is OperationCanceledException or ObjectDisposedException);

    /// <summary>经 UI 桥取本地化文案；宿主未提供实现时回退中文原文。</summary>
    private string Text(string key) => _uiBridge?.GetText(key) ?? key;

    /// <summary>
    /// 把传输状态同步到主界面列表（无 UI 桥时安全忽略）。
    /// UI 回调属于「展示层副作用」，绝不能因为它的异常中断真正的发送 / 收包流程。
    /// </summary>
    private void Report(ExternalTransferUpdate update)
    {
        if (_uiBridge is null)
        {
            return;
        }

        try
        {
            _uiBridge.ReportTransfer(update);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上报传输状态到界面失败: {TransferId}", update.TransferId);
        }
    }

    /// <inheritdoc />
    public bool IsRunning => _server is not null;

    /// <inheritdoc />
    public string? Endpoint { get; private set; }

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public event Action<string>? FileReceived;

    /// <inheritdoc />
    public event Action<LocalSendPeer>? PeerDiscovered;

    /// <inheritdoc />
    public IReadOnlyCollection<LocalSendPeer> Peers => _peers.Values.ToList();

    /// <inheritdoc />
    public void Apply()
    {
        var settings = _settingsService.Current;
        // 只有影响 LocalSend 服务本身的配置变化才重启（设备名会写进组播宣告 / info）
        var signature = string.Join('\u001f',
            settings.LocalSendEnabled,
            settings.LocalSendPort,
            settings.LocalSendPin,
            settings.DeviceName);

        if (_appliedSignature == signature && IsRunning)
        {
            _logger.LogDebug("LocalSend 配置未变化，跳过重启");
            return;
        }

        Stop();

        _appliedSignature = signature;
        if (!settings.LocalSendEnabled)
        {
            return;
        }

        var port = settings.LocalSendPort is >= 1024 and <= 65535 ? settings.LocalSendPort : DefaultPort;

        try
        {
            var dataDirectory = Path.GetDirectoryName(_settingsService.SettingsFilePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dataDirectory);
            _identity = LocalSendIdentity.LoadOrCreate(Path.Combine(dataDirectory, "localsend-identity.json"));
            _trustStore = LocalSendTrustStore.LoadOrCreate(Path.Combine(dataDirectory, "localsend-trusted.json"));

            var clientInfo = new ClientInfo
            {
                Alias = settings.DeviceName,
                Version = ProtocolVersion,
                DeviceType = global::LocalSend.Core.Model.DeviceType.Desktop,

                // 指纹 = 证书 DER 的 SHA-256（与官方一致）；既做设备身份，也用于组播过滤自己的消息
                Token = _identity.Fingerprint
            };

            var v2Config = new ServerConfigV2
            {
                Pin = string.IsNullOrWhiteSpace(settings.LocalSendPin) ? null : settings.LocalSendPin.Trim()
            };

            // 先确认本机证书能否被 Schannel 使用（私钥必须能落到密钥容器）。
            // 不能的话就退化为 HTTP 服务端 + 宣告 http，至少保证互通可用（而不是宣告 https 却握手失败）。
            var useHttps = true;
            try
            {
                // 统一走 FileShare.Crypto 的共享加载器（与 FileShare 自身 TLS 同一条路径）
                using var probeCertificate = TlsCertificateLoader.LoadWithPrivateKey(
                    _identity.CertPem, _identity.PrivateKeyPem, trace: LsLog.Trace);
                useHttps = !TlsCertificateLoader.LastLoadUsedEphemeralKey;
            }
            catch (Exception ex)
            {
                useHttps = false;
                _logger.LogWarning(ex, "校验本机 LocalSend 证书失败，退化为 HTTP");
            }

            var tlsConfig = useHttps
                ? new TlsConfig { Cert = _identity.CertPem, PrivateKey = _identity.PrivateKeyPem }
                : null;

            if (!useHttps)
            {
                _logger.LogWarning("本机无法持久化证书私钥，LocalSend 退化为 HTTP（无加密）");
                _uiBridge?.ShowToast(
                    Text("LocalSend HTTPS 不可用"),
                    Text("无法写入证书私钥（权限不足），已退化为 HTTP；可在官方客户端关闭加密后使用"));
            }

            _server = new LocalSendServer(port: (ushort)port, tlsConfig: tlsConfig, info: clientInfo, internalConfig: null, v2Config: v2Config, webSendConfig: null);
            _cts = new CancellationTokenSource();
            _sender = new LocalSendSender(
                settings.DeviceName,
                _identity,
                _trustStore,
                _loggerFactory.CreateLogger<LocalSendSender>());

            // 组播响应走 HTTPS（携带本机证书），否则官方客户端无法回连
            _discoveryClient = new LsHttpClientV2(_identity.PrivateKeyPem, _identity.CertPem);

            Observe(_server.StartAsync(_cts.Token), "LocalSend 服务监听");
            _eventLoop = Observe(Task.Run(() => RunEventLoopAsync(_server, _cts.Token)), "LocalSend 事件循环");

            // 组播宣告：让官方 LocalSend 客户端能发现本机
            try
            {
                _multicast = new MulticastDiscoveryService(
                    localInfo: clientInfo,
                    httpPort: (ushort)port,
                    protocol: useHttps ? ProtocolTypeV2.Https : ProtocolTypeV2.Http,
                    multicastGroup: MulticastDiscoveryService.DefaultMulticastGroup,
                    multicastPort: MulticastDiscoveryService.DefaultMulticastPort,
                    serverRunning: true,
                    httpClient: _discoveryClient);

                _multicast.DeviceDiscovered += OnPeerDiscovered;

                Observe(_multicast.StartListeningAsync(_cts.Token), "LocalSend 组播监听");
                Observe(_multicast.SendAnnouncementAsync(_cts.Token), "LocalSend 组播宣告");
            }
            catch (Exception ex)
            {
                // 组播失败不影响接收（用户仍可在官方客户端里手动指定 IP）
                _logger.LogWarning(ex, "LocalSend 组播宣告失败");
            }


            Endpoint = $"{(useHttps ? "https" : "http")}://{LocalAddress() ?? "127.0.0.1"}:{port}";
            LastError = null;
            _logger.LogInformation("LocalSend 互通已启用: {Endpoint}", Endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 LocalSend 互通失败（端口 {Port} 可能被占用）", port);
            Stop();
            LastError = ex.Message;
            _uiBridge?.ShowToast(Text("LocalSend 互通启动失败"), ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<LocalSendPeer?> ProbePeerAsync(string ipAddress, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || port is < 1 or > 65535 || _sender is null)
        {
            return null;
        }

        // 先试 HTTPS（mTLS + TOFU），失败再退 HTTP
        var peer = await _sender.ProbeAsync(ipAddress, port, cancellationToken).ConfigureAwait(false);
        if (peer is null)
        {
            return null;
        }

        var key = string.IsNullOrEmpty(peer.Fingerprint) ? $"{ipAddress}:{port}" : peer.Fingerprint;
        if (_peers.TryAdd(key, peer))
        {
            _logger.LogInformation(
                "通过 IP 发现 LocalSend 设备: {Alias} ({Ip}:{Port}, {Protocol})",
                peer.Alias, peer.IpAddress, peer.Port, peer.Protocol);
            PeerDiscovered?.Invoke(peer);
        }

        return peer;
    }

    /// <summary>
    /// 把官方客户端注册信息并入设备列表（去重后触发 <see cref="PeerDiscovered"/>）。
    /// </summary>
    private bool RegisterPeerFromDto(IPAddress? ip, RegisterDtoV2 info, string source)
    {
        if (ip is null || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.None))
        {
            return false;
        }

        var fingerprint = string.IsNullOrWhiteSpace(info.Fingerprint) ? ip.ToString() : info.Fingerprint;
        var key = fingerprint;

        if (_peers.ContainsKey(key))
        {
            return false;
        }


        var peer = new LocalSendPeer(
            DeviceId: "localsend:" + key,
            Alias: string.IsNullOrWhiteSpace(info.Alias) ? ip.ToString() : info.Alias,
            IpAddress: ip.ToString(),
            Port: info.Port == 0 ? (ushort)DefaultPort : info.Port,
            Protocol: info.Protocol,
            Fingerprint: fingerprint,
            DeviceType: info.DeviceType ?? global::LocalSend.Core.Model.DeviceType.Desktop);

        if (!_peers.TryAdd(key, peer))
        {
            return false;
        }

        _logger.LogInformation(
            "发现 LocalSend 设备（{Source}）: {Alias} ({Ip}:{Port}, {Protocol})",
            source, peer.Alias, peer.IpAddress, peer.Port, peer.Protocol);
        PeerDiscovered?.Invoke(peer);
        return true;
    }
    private void OnPeerDiscovered(object? sender, MulticastPeerInfo info)
    {
        if (info.IP is null || info.IP.Equals(IPAddress.Any) || info.IP.Equals(IPAddress.None))
        {
            return;
        }

        var key = string.IsNullOrEmpty(info.Fingerprint) ? info.IP.ToString() : info.Fingerprint;
        if (_peers.ContainsKey(key))
        {
            return;
        }


        var peer = new LocalSendPeer(
            DeviceId: "localsend:" + key,
            Alias: string.IsNullOrWhiteSpace(info.Alias) ? info.IP.ToString() : info.Alias,
            IpAddress: info.IP.ToString(),
            Port: info.Port,
            Protocol: info.Protocol,
            Fingerprint: info.Fingerprint,
            DeviceType: info.DeviceType ?? global::LocalSend.Core.Model.DeviceType.Desktop);

        if (_peers.TryAdd(key, peer))
        {
            _logger.LogInformation("发现 LocalSend 设备: {Alias} ({Ip}:{Port})", peer.Alias, peer.IpAddress, peer.Port);
            PeerDiscovered?.Invoke(peer);
        }
    }

    /// <inheritdoc />
    public async Task<LocalSendSendResult> SendFileAsync(
        LocalSendPeer peer,
        string filePath,
        string? relativePath = null,
        Action<ulong, ulong>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sender = _sender;
        if (sender is null)
        {
            var reason = Text("LocalSend 互通未启用");
            _logger.LogWarning("LocalSend 文件发送失败：{Reason} → {Alias}", reason, peer.Alias);
            // 与文本消息一致：失败也要留下记录（源文件仍在本地，历史页能出预览）
            await RecordFileSendHistoryAsync(filePath, peer, TransferStatus.Failed, reason).ConfigureAwait(false);
            return new LocalSendSendResult(false, reason);
        }

        // 发送方向同样要在主界面传输列表里显示（进度 / 完成 / 失败）
        var transferId = "ls-send:" + Guid.NewGuid().ToString("N");
        var displayName = string.IsNullOrWhiteSpace(relativePath) ? Path.GetFileName(filePath) : relativePath!;
        long fileSize = 0;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
            {
                fileSize = info.Length;
            }
        }
        catch
        {
            // 大小只用于展示
        }

        Report(ExternalTransferUpdate.For(transferId, displayName, fileSize,
            TransferDirection.Send, TransferStatus.Transferring, senderId: "local-device"));

        Action<ulong, ulong> combinedProgress = (sent, total) =>
        {
            Report(ExternalTransferUpdate.For(transferId, displayName, (long)total,
                TransferDirection.Send, TransferStatus.Transferring, (long)sent, "local-device"));
            progress?.Invoke(sent, total);
        };

        LocalSendSendResult result;
        try
        {
            result = await sender.SendAsync(peer, filePath, relativePath, combinedProgress, cancellationToken)
                .ConfigureAwait(false);
        }
            catch (Exception ex)
            {
                var status = SendFailureStatus(cancellationToken);
                Report(ExternalTransferUpdate.For(transferId, displayName, fileSize,
                    TransferDirection.Send, status, senderId: "local-device"));
                await RecordFileSendHistoryAsync(filePath, peer, status, ex.Message).ConfigureAwait(false);
                _logger.LogWarning(ex, "LocalSend 文件发送异常: {Path} → {Alias}", filePath, peer.Alias);
                // 不要再往外抛：异常从 UI 命令冒泡出去只会变成 Unobserved，
                // 历史已记录、终态已上报，返回失败结果让上层显示原因即可
                return new LocalSendSendResult(false, ex.Message);
            }

        var finalStatus = result.Success ? TransferStatus.Completed : SendFailureStatus(cancellationToken);
        Report(ExternalTransferUpdate.For(transferId, displayName, fileSize,
            TransferDirection.Send, finalStatus,
            result.Success ? fileSize : 0, "local-device"));

        // 成功 / 失败 / 取消都记录；发送方向源文件仍在本地，失败记录也能出缩略图预览
        if (result.Success)
        {
            await RecordSendHistoryAsync(filePath, peer).ConfigureAwait(false);
        }
        else
        {
            await RecordFileSendHistoryAsync(filePath, peer, finalStatus, result.Error).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>发送失败的终态分类：本方请求了取消 → 已取消，其余 → 失败。</summary>
    private static TransferStatus SendFailureStatus(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? TransferStatus.Cancelled : TransferStatus.Failed;

    /// <summary>
    /// 记录一条 LocalSend 文件发送失败/取消历史（成功路径由 <see cref="RecordSendHistoryAsync"/> 处理）。
    /// 源文件仍在本地，保留其绝对路径以便历史页显示预览、事后重发。
    /// </summary>
    private async Task RecordFileSendHistoryAsync(string filePath, LocalSendPeer peer, TransferStatus status, string? error)
    {
        if (_databaseService is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(filePath);
            var entity = new ReceiveHistoryEntity
            {
                SenderId = "local-device",
                SenderDeviceName = _settingsService.Current.DeviceName,
                PeerId = peer.DeviceId,
                PeerName = peer.Alias,
                FileName = info.Name,
                FileSize = info.Exists ? info.Length : 0,
                SavePath = info.DirectoryName ?? string.Empty,
                FilePath = filePath,
                Status = status,
                ErrorMessage = error,
                CreatedAt = DateTime.UtcNow,
                Direction = TransferDirection.Send
            };

            await _databaseService.AddSingleReceiveHistoryAsync(entity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 LocalSend 文件发送失败历史失败: {Path}", filePath);
        }
    }

    private async Task RecordSendHistoryAsync(string filePath, LocalSendPeer peer)
    {
        if (_databaseService is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(filePath);
            var entity = new ReceiveHistoryEntity
            {
                SenderId = "local-device",
                SenderDeviceName = _settingsService.Current.DeviceName,
                PeerId = peer.DeviceId,
                PeerName = peer.Alias,
                FileName = info.Name,
                FileSize = info.Exists ? info.Length : 0,
                SavePath = info.DirectoryName ?? string.Empty,
                FilePath = filePath,
                Status = TransferStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                Direction = TransferDirection.Send
            };

            await _databaseService.AddSingleReceiveHistoryAsync(entity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 LocalSend 发送历史失败: {Path}", filePath);
        }
    }
    /// <summary>
    /// 判定一个 FileDto 是不是「文本消息」。
    /// <para>
    /// 官方客户端断点实证：发文本消息时 <c>Preview</c> 非空（承载消息正文），
    /// 发文本文件时 <c>Preview</c> 为 null；图片的 Preview 也非空（缩略图），
    /// 因此必须同时满足 <c>FileType</c> 为 <c>text/*</c> 且 <c>Preview</c> 非空。
    /// </para>
    /// </summary>
    internal static bool IsTextMessage(FileDto file) =>
        !string.IsNullOrEmpty(file.Preview) &&
        (file.FileType ?? string.Empty).StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 Preview 正文：原文优先；仅当它明显是 base64 且解码后是合法 UTF-8 文本时，
    /// 才用 base64 解码结果（兼容个别官方版本把正文按字节编码的做法）。
    /// </summary>
    internal static string DecodeMessagePreview(string? preview)
    {
        if (string.IsNullOrEmpty(preview))
        {
            return string.Empty;
        }

        try
        {
            var raw = preview;
            if (raw.Length >= 8 && raw.Length % 4 == 0 && IsBase64Alphabet(raw))
            {
                var bytes = Convert.FromBase64String(raw);
                var decoded = StrictUtf8.GetString(bytes);
                if (!string.IsNullOrWhiteSpace(decoded) &&
                    !decoded.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
                {
                    return decoded;
                }
            }
        }
        catch (Exception)
        {
            // 不是合法 base64 / 不是合法 UTF-8：按原文处理
        }

        return preview;
    }

    private static bool IsBase64Alphabet(string value)
    {
        foreach (var c in value)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '='))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>文本消息在传输列表 / 历史里显示的摘要（单行、无路径字符）。</summary>
    internal static string MessageSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "文本消息";
        }

        var singleLine = text
            .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')
            .Replace('/', ' ').Replace('\\', ' ').Replace(':', ' ').Replace('\0', ' ')
            .Trim();

        return singleLine.Length switch
        {
            0 => "文本消息",
            > 24 => singleLine[..24] + "…",
            _ => singleLine
        };
    }

        /// <inheritdoc />
        public async Task<LocalSendSendResult> SendMessageAsync(
            LocalSendPeer peer,
            string text,
            CancellationToken cancellationToken = default)
        {
            var sender = _sender;
            if (sender is null)
            {
                _logger.LogWarning("LocalSend 文本消息发送失败：互通未启用 → {Alias}", peer.Alias);
                await RecordMessageSendHistoryAsync(text, peer, TransferStatus.Failed, Text("LocalSend 互通未启用"))
                    .ConfigureAwait(false);
                return new LocalSendSendResult(false, Text("LocalSend 互通未启用"));
            }

            var transferId = "ls-send:" + Guid.NewGuid().ToString("N");
            var size = Encoding.UTF8.GetByteCount(text);
            var summary = MessageSummary(text);

            _logger.LogInformation("LocalSend 文本消息开始发送: {Summary} → {Alias} ({Size} 字节)",
                summary, peer.Alias, size);

            Report(ExternalTransferUpdate.ForMessage(transferId, summary, text, size,
                TransferDirection.Send, TransferStatus.Transferring, "local-device"));

            LocalSendSendResult result;
            try
            {
                result = await sender.SendMessageAsync(peer, text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var status = SendFailureStatus(cancellationToken);
                _logger.LogWarning(ex, "LocalSend 文本消息发送异常: → {Alias} ({Status})", peer.Alias, status);
                Report(ExternalTransferUpdate.ForMessage(transferId, summary, text, size,
                    TransferDirection.Send, status, "local-device"));
                await RecordMessageSendHistoryAsync(text, peer, status, ex.Message).ConfigureAwait(false);
                return new LocalSendSendResult(false, ex.Message);
            }

            var finalStatus = result.Success ? TransferStatus.Completed : SendFailureStatus(cancellationToken);
            _logger.LogInformation("LocalSend 文本消息发送结果: {Summary} → {Alias} = {Status}{Reason}",
                summary, peer.Alias, finalStatus,
                string.IsNullOrEmpty(result.Error) ? string.Empty : $" ({result.Error})");

            Report(ExternalTransferUpdate.ForMessage(transferId, summary, text, size,
                TransferDirection.Send, finalStatus, "local-device"));

            // 成功 / 失败 / 取消都记录：否则「数据已送达对端但客户端判定失败」时历史里毫无痕迹
            await RecordMessageSendHistoryAsync(text, peer, finalStatus, result.Success ? null : result.Error)
                .ConfigureAwait(false);

            return result;
        }

    /// <summary>
    /// 记录一条 LocalSend 文本消息发送历史（所有终态都记，失败时带原因）。
    /// <para>
    /// 文本消息与文件发送请求是两类不同的记录：这里做最后一道净化，
    /// 保证文本消息记录永远不会带上「对方未接受文件」「对方拒绝了该文件」
    /// 这类文件语义的失败原因，且成功记录一定没有 ErrorMessage。
    /// </para>
    /// </summary>
    private async Task RecordMessageSendHistoryAsync(string text, LocalSendPeer peer, TransferStatus status, string? error)
    {
        if (_databaseService is null)
        {
            return;
        }

        // 成功 = 已送达，历史里不留任何失败原因
        if (status == TransferStatus.Completed)
        {
            error = null;
        }
        else if (!string.IsNullOrEmpty(error) &&
                 (error.Contains("对方未接受文件", StringComparison.Ordinal) ||
                  error.Contains("对方拒绝了该文件", StringComparison.Ordinal)))
        {
            _logger.LogWarning("文本消息记录收到文件语义失败原因，已替换为中性文案: {Reason}", error);
            error = Text("文本消息发送失败");
        }

        try
        {
            var entity = new ReceiveHistoryEntity
            {
                SenderId = "local-device",
                SenderDeviceName = _settingsService.Current.DeviceName,
                PeerId = peer.DeviceId,
                PeerName = peer.Alias,
                FileName = MessageSummary(text),
                FileSize = Encoding.UTF8.GetByteCount(text),
                SavePath = string.Empty,
                FilePath = null,
                Status = status,
                ErrorMessage = error,
                CreatedAt = DateTime.UtcNow,
                Direction = TransferDirection.Send,
                Message = text
            };

            await _databaseService.AddSingleReceiveHistoryAsync(entity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 LocalSend 文本消息发送历史失败");
        }
    }

    /// <summary>
    /// 处理 prepare-upload：登记传输项 → 请求用户确认（收藏设备可免确认）→ 回传决策。
    /// 独立任务执行，避免 60 秒确认窗口阻塞服务端事件循环；任何异常都必须回一个决策，
    /// 否则 HTTP 请求会一直挂起。
    /// </summary>
    private async Task HandlePrepareUploadAsync(ServerEventV2.PrepareUpload prepare)
    {
        try
        {
            // 发送方本身也是可回连的设备
            RegisterPeerFromDto(prepare.Ip, prepare.Info, "上传请求");

            var senderId = "localsend:" +
                           (string.IsNullOrWhiteSpace(prepare.Info.Fingerprint)
                               ? prepare.Ip.ToString()
                               : prepare.Info.Fingerprint);
            var senderName = string.IsNullOrWhiteSpace(prepare.Info.Alias)
                ? prepare.Ip.ToString()
                : prepare.Info.Alias;
            _sessions[prepare.SessionId] = new PeerSessionInfo(senderId, senderName);

            var saveDirectory = string.IsNullOrWhiteSpace(_settingsService.Current.SaveDirectory)
                ? _directoryService.GetDownloadsDirectory()
                : _settingsService.Current.SaveDirectory!;

            // 1) 文本消息：跳过确认流程，立即显示为「已完成」并写历史（不落盘）
            var messageIds = new HashSet<string>();
            foreach (var (fileId, file) in prepare.Files)
            {
                if (!IsTextMessage(file))
                {
                    continue;
                }

                messageIds.Add(fileId);
                await HandleIncomingMessageAsync(prepare.SessionId, fileId, file, senderId, senderName)
                    .ConfigureAwait(false);
            }

            var fileItems = prepare.Files
                .Where(pair => !IsTextMessage(pair.Value))
                .ToList();

            // 纯文本消息：直接 Accept（含该 fileId），官方客户端随后照常上传字节
            if (fileItems.Count == 0)
            {
                prepare.DecisionTx.TrySetResult(
                    new PrepareUploadDecisionV2.Accept(prepare.Files.Keys.ToHashSet()));
                _logger.LogInformation("LocalSend 文本消息已接收: {Alias}（{Count} 条）",
                    senderName, messageIds.Count);
                return;
            }

            // 2) 文件项：先在传输列表里显示「等待确认」
            foreach (var (fileId, file) in fileItems)
            {
                Report(ExternalTransferUpdate.For(
                    TransferIdFor(prepare.SessionId, fileId), file.FileName, (long)file.Size,
                    TransferDirection.Receive, TransferStatus.Pending, senderId: senderId));
            }

            var totalSize = fileItems.Sum(pair => (long)pair.Value.Size);
            var description = fileItems.Count > 1
                ? $"共 {fileItems.Count} 个文件：" +
                  string.Join("、", fileItems.Take(3).Select(pair => pair.Value.FileName)) +
                  (fileItems.Count > 3 ? "…" : "")
                : fileItems[0].Value.FileName;

            // 与 FileShare 自有协议一致：非收藏设备需用户确认，收藏设备可免确认
            ExternalReceiveDecision decision;
            try
            {
                decision = _uiBridge is null
                    ? ExternalReceiveDecision.AcceptOnce // 无界面宿主（测试）保持原行为
                    : await _uiBridge.ConfirmReceiveAsync(new ExternalReceiveRequest(
                        senderId, senderName, description, totalSize, saveDirectory))
                        .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalSend 接收确认失败，拒绝本次上传");
                decision = ExternalReceiveDecision.Declined;
            }

            if (!decision.Accept)
            {
                foreach (var (fileId, file) in fileItems)
                {
                    Report(ExternalTransferUpdate.For(
                        TransferIdFor(prepare.SessionId, fileId), file.FileName, (long)file.Size,
                        TransferDirection.Receive, TransferStatus.Cancelled, senderId: senderId));
                }

                // 混合会话里消息已经接收，仍要接受这些 fileId 让对端完成握手；否则整单拒绝
                prepare.DecisionTx.TrySetResult(messageIds.Count > 0
                    ? new PrepareUploadDecisionV2.Accept(messageIds)
                    : new PrepareUploadDecisionV2.Decline());
                _logger.LogInformation("LocalSend 上传请求被拒绝: {Alias}", senderName);
                return;
            }

            prepare.DecisionTx.TrySetResult(new PrepareUploadDecisionV2.Accept(prepare.Files.Keys.ToHashSet()));
            _logger.LogInformation(
                "LocalSend 上传请求已接受: {Alias}（{Count} 个文件，{MessageCount} 条消息）",
                senderName, fileItems.Count, messageIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 LocalSend 上传请求失败");
            prepare.DecisionTx.TrySetResult(new PrepareUploadDecisionV2.Decline());
        }
    }

    /// <summary>
    /// 文本消息在 prepare-upload 阶段就已完整到达：直接进传输列表、写接收历史、弹通知。
    /// 随后的 /upload 只是协议握手，字节会写到临时文件并立即删除。
    /// </summary>
    private async Task HandleIncomingMessageAsync(
        string sessionId, string fileId, FileDto file,
        string senderId, string senderName)
    {
        var text = DecodeMessagePreview(file.Preview);
        var transferId = TransferIdFor(sessionId, fileId);
        _messageTransfers[transferId] = 0;

        var size = Encoding.UTF8.GetByteCount(text);
        var summary = MessageSummary(text);

        Report(ExternalTransferUpdate.ForMessage(transferId, summary, text, size,
            TransferDirection.Receive, TransferStatus.Completed, senderId));

        try
        {
            var entity = new ReceiveHistoryEntity
            {
                SenderId = senderId,
                SenderDeviceName = senderName,
                PeerId = senderId,
                PeerName = senderName,
                FileName = summary,
                FileSize = size,
                SavePath = string.Empty,
                FilePath = null,
                Status = TransferStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                Direction = TransferDirection.Receive,
                Message = text
            };

            await _databaseService.AddSingleReceiveHistoryAsync(entity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 LocalSend 文本消息历史失败");
        }

        // 弹窗展示全文 + 复制（替代原来只在传输列表里显示的 Toast）
        _uiBridge?.ShowTextMessage(senderName, text);
    }

    private async Task RunEventLoopAsync(LocalSendServer server, CancellationToken cancellationToken)
    {
        var v2 = server.State.V2;
        if (v2 is null)
        {
            return;
        }

        try
        {
            await foreach (var evt in v2.EventTx.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case ServerEventV2.Register register:
                        // 官方客户端主动注册到我们这里 —— 这是最可靠的发现来源，直接并入设备列表
                        if (RegisterPeerFromDto(register.Ip, register.Info, "注册"))
                        {
                            _logger.LogInformation("LocalSend 设备注册并加入列表: {Alias} ({Ip})", register.Info.Alias, register.Ip);
                        }
                        else
                        {
                            // 已知设备会周期性重复注册，降为 Debug，避免刷日志
                            _logger.LogDebug("LocalSend 设备重复注册: {Alias} ({Ip})", register.Info.Alias, register.Ip);
                        }

                        break;

                    case ServerEventV2.PrepareUpload prepare:
                        // 确认弹窗可能等待 60 秒，放到独立任务里，避免阻塞事件循环
                        _ = HandlePrepareUploadAsync(prepare);
                        break;

                    case ServerEventV2.FileUpload upload:
                        HandleFileUpload(upload);
                        break;

                    case ServerEventV2.SessionEnd ended:
                        _sessions.TryRemove(ended.SessionId, out _);
                        var prefix = $"ls-recv:{ended.SessionId}:";
                        foreach (var key in _messageTransfers.Keys
                                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                                     .ToArray())
                        {
                            _messageTransfers.TryRemove(key, out _);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 LocalSend 事件失败");
        }
    }

    private void HandleFileUpload(ServerEventV2.FileUpload upload)
    {
        var transferId = TransferIdFor(upload.SessionId, upload.FileId);
        var peer = _sessions.TryGetValue(upload.SessionId, out var info)
            ? info
            : new PeerSessionInfo("localsend", "LocalSend");
        var fileSize = (long)upload.File.Size;

        // 文本消息：正文已在 prepare-upload 阶段拿到，这里只完成官方客户端的上传握手，
        // 字节写到临时文件后立刻删除，绝不落到用户的保存目录
        if (_messageTransfers.ContainsKey(transferId) || IsTextMessage(upload.File))
        {
            HandleMessageUpload(upload, transferId);
            return;
        }

        try
        {
            var directory = string.IsNullOrWhiteSpace(_settingsService.Current.SaveDirectory)
                ? _directoryService.GetDownloadsDirectory()
                : _settingsService.Current.SaveDirectory!;

            Directory.CreateDirectory(directory);

            var fileName = FileNameHelper.SanitizeFileName(upload.File.FileName);
            var targetPath = FileNameHelper.ResolveTargetPath(directory, fileName, FileNameConflictPolicy.Rename);

            var resultTx = new TaskCompletionSource<Result<Unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            upload.TargetTx.TrySetResult(new FileUploadTarget.PathTarget(targetPath, resultTx));

            Report(ExternalTransferUpdate.For(transferId, fileName, fileSize,
                TransferDirection.Receive, TransferStatus.Transferring,
                senderId: peer.SenderId, savePath: targetPath));

            _ = WatchProgressAsync(transferId, fileName, targetPath, fileSize, peer, resultTx.Task);
            _ = WatchAsync(resultTx, targetPath, directory, fileName, transferId, fileSize, peer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "准备保存 LocalSend 文件失败");
            Report(ExternalTransferUpdate.For(transferId, upload.File.FileName, fileSize,
                TransferDirection.Receive, TransferStatus.Failed, senderId: peer.SenderId));
            upload.TargetTx.TrySetResult(new FileUploadTarget.PathTarget(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                new TaskCompletionSource<Result<Unit, string>>()));
        }
    }

    /// <summary>文本消息的上传握手：返回一个临时路径，写入完成后删除。</summary>
    private void HandleMessageUpload(ServerEventV2.FileUpload upload, string transferId)
    {
        try
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "FileShare.LocalSend.Messages");
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");

            var resultTx = new TaskCompletionSource<Result<Unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            upload.TargetTx.TrySetResult(new FileUploadTarget.PathTarget(tempPath, resultTx));

            _ = WatchMessageUploadAsync(resultTx.Task, tempPath, transferId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "准备文本消息上传握手失败");
            upload.TargetTx.TrySetResult(new FileUploadTarget.PathTarget(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                new TaskCompletionSource<Result<Unit, string>>()));
        }
    }

    private async Task WatchMessageUploadAsync(Task completion, string tempPath, string transferId)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 握手失败也要清理
        }
        finally
        {
            _messageTransfers.TryRemove(transferId, out _);
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception)
            {
                // 临时文件清理失败不影响消息本身
            }
        }
    }

    /// <summary>轮询落盘文件大小，把接收进度刷到传输列表。</summary>
    private async Task WatchProgressAsync(
        string transferId, string fileName, string targetPath,
        long fileSize, PeerSessionInfo peer, Task completion)
    {
        try
        {
            while (!completion.IsCompleted)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (completion.IsCompleted)
                {
                    break;
                }

                long written = 0;
                try
                {
                    if (File.Exists(targetPath))
                    {
                        written = new FileInfo(targetPath).Length;
                    }
                }
                catch
                {
                    // 文件尚未创建 / 正在写入，忽略本次采样
                }

                Report(ExternalTransferUpdate.For(transferId, fileName, fileSize,
                    TransferDirection.Receive, TransferStatus.Transferring,
                    Math.Min(written, fileSize), peer.SenderId, targetPath));
            }
        }
        catch
        {
            // 进度上报失败不影响接收
        }
    }

    private async Task WatchAsync(
        TaskCompletionSource<Result<Unit, string>> resultTx,
        string targetPath,
        string directory,
        string fileName,
        string transferId,
        long fileSize,
        PeerSessionInfo peer)
    {
        try
        {
            var result = await resultTx.Task.ConfigureAwait(false);
            if (!result.IsOk)
            {
                _logger.LogWarning("LocalSend 文件保存失败: {Path} ({Error})", targetPath, result.Error);
                Report(ExternalTransferUpdate.For(transferId, fileName, fileSize,
                    TransferDirection.Receive, TransferStatus.Failed, senderId: peer.SenderId));
                return;
            }

            _logger.LogInformation("LocalSend 收到文件: {Path}", targetPath);
            Report(ExternalTransferUpdate.For(transferId, fileName, fileSize,
                TransferDirection.Receive, TransferStatus.Completed, fileSize,
                peer.SenderId, targetPath));

            var entity = new ReceiveHistoryEntity
            {
                // 使用真实对端身份（LocalSend 设备别名），而不是固定字符串 "localsend"
                SenderId = peer.SenderId,
                SenderDeviceName = peer.SenderName,
                PeerId = peer.SenderId,
                PeerName = peer.SenderName,
                FileName = Path.GetFileName(targetPath),
                FileSize = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0,
                SavePath = directory,
                FilePath = targetPath,
                Status = TransferStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                Direction = TransferDirection.Receive
            };

            _ = _databaseService.AddSingleReceiveHistoryAsync(entity);

            FileReceived?.Invoke(targetPath);
            _uiBridge?.ShowToast(Text("收到 LocalSend 文件"), $"{fileName}（已保存到 {directory}）");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "等待 LocalSend 文件写入结果失败");
        }
    }

    private static string? LocalAddress()
    {
        try
        {
            var addresses = global::FileShare.Core.Network.LocalNetworkHelper.GetLocalIPv4Addresses();
            return addresses.Count > 0 ? addresses[0].ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Stop()
    {
        // 清空签名：启动失败时下一次 Apply 必须能重新尝试
        _appliedSignature = null;

        try
        {
            _cts?.Cancel();
        }
        catch (Exception)
        {
            // 忽略
        }

        try
        {
            _multicast?.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }

        _sender = null;
        _discoveryClient?.Dispose();
        _discoveryClient = null;
        _peers.Clear();
        _sessions.Clear();

        try
        {
            _server?.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }

        _multicast = null;
        _server = null;
        _cts?.Dispose();
        _cts = null;
        _eventLoop = null;
        Endpoint = null;
        LastError = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();
}
