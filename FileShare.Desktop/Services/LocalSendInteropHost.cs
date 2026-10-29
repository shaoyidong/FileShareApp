using FileShare.Core.Common;
using FileShare.Desktop.Localization;
using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using LocalSend.Core.Discovery;
using LocalSend.Core.Http.Client;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Crypto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.State;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services;

/// <summary>
/// LocalSend 协议互通（接收方向）：
/// <list type="bullet">
/// <item>用 <c>LocalSendServer</c> 在本机启动 LocalSend v2 HTTP 服务（默认 53317），
/// 官方 LocalSend 客户端可以把文件直接发到本机；</item>
/// <item>用 <c>MulticastDiscoveryService</c> 发送组播宣告，让官方客户端能在设备列表里发现本机
/// （否则对方根本不知道我们存在）。</item>
/// </list>
/// <para>注意：官方客户端的 HTTPS/mTLS 指纹校验、PIN 交互与发送方向尚未接入，
/// 见 docs/LocalSend-功能对照与差距分析.md。</para>
/// </summary>
public interface ILocalSendInteropHost : IDisposable
{
    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>本机 LocalSend 端点（例如 http://192.168.1.10:53317）。</summary>
    string? Endpoint { get; }

    /// <summary>最近一次启动失败的原因（成功时为 null）。</summary>
    string? LastError { get; }

    /// <summary>按当前设置启动或停止。</summary>
    void Apply();

    /// <summary>收到文件时触发（已落盘的绝对路径）。</summary>
    event Action<string>? FileReceived;

    /// <summary>通过 LocalSend 组播发现到新设备时触发。</summary>
    event Action<LocalSendPeer>? PeerDiscovered;

    /// <summary>已发现的 LocalSend 设备。</summary>
    IReadOnlyCollection<LocalSendPeer> Peers { get; }

    /// <summary>
    /// 用 IP + 端口探测对端是否是 LocalSend 设备（跨子网 / 组播被拦时的兜底）。
    /// </summary>
    Task<LocalSendPeer?> ProbePeerAsync(string ipAddress, int port, CancellationToken cancellationToken = default);

    /// <summary>发送单个文件到 LocalSend 设备。</summary>
    Task<LocalSendSendResult> SendFileAsync(
        LocalSendPeer peer,
        string filePath,
        string? relativePath = null,
        Action<ulong, ulong>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILocalSendInteropHost"/>
public sealed class LocalSendInteropHost : ILocalSendInteropHost
{
    private const int DefaultPort = 53317;
    private const string ProtocolVersion = "2.3";

    private readonly IAppSettingsService _settingsService;
    private readonly IPlatformDirectoryService _directoryService;
    private readonly IDatabaseService _databaseService;
    private readonly IToastService? _toastService;
    private readonly ILogger<LocalSendInteropHost> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly ConcurrentDictionary<string, LocalSendPeer> _peers = new();
    private LsHttpClientV2? _discoveryClient;
    private LocalSendSender? _sender;
    private LocalSendIdentity? _identity;
    private LocalSendTrustStore? _trustStore;
    private LocalSendServer? _server;
    private MulticastDiscoveryService? _multicast;
    private CancellationTokenSource? _cts;
    private Task? _eventLoop;

    public LocalSendInteropHost(
        IAppSettingsService settingsService,
        IPlatformDirectoryService directoryService,
        IDatabaseService databaseService,
        ILoggerFactory loggerFactory,
        IToastService? toastService = null)
    {
        _settingsService = settingsService;
        _directoryService = directoryService;
        _databaseService = databaseService;
        _toastService = toastService;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LocalSendInteropHost>();
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
        Stop();

        var settings = _settingsService.Current;
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
                DeviceType = LocalSend.Core.Model.DeviceType.Desktop,

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
                using var probeCertificate = TlsCertificateLoader.LoadWithPrivateKey(_identity.CertPem, _identity.PrivateKeyPem);
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
                _toastService?.Show(
                    Loc.Get("LocalSend HTTPS 不可用"),
                    Loc.Get("无法写入证书私钥（权限不足），已退化为 HTTP；可在官方客户端关闭加密后使用"));
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

            _ = _server.StartAsync(_cts.Token);
            _eventLoop = Task.Run(() => RunEventLoopAsync(_server, _cts.Token));

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

                _ = _multicast.StartListeningAsync(_cts.Token);
                _ = _multicast.SendAnnouncementAsync(_cts.Token);
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
            _toastService?.Show(Loc.Get("LocalSend 互通启动失败"), ex.Message);
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
            DeviceType: info.DeviceType ?? LocalSend.Core.Model.DeviceType.Desktop);

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
            DeviceType: info.DeviceType ?? LocalSend.Core.Model.DeviceType.Desktop);

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
            return new LocalSendSendResult(false, Loc.Get("LocalSend 互通未启用"));
        }

        var result = await sender.SendAsync(peer, filePath, relativePath, progress, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            _ = RecordSendHistoryAsync(filePath, peer);
        }

        return result;
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
                        // 发送方本身也是可回连的设备
                        RegisterPeerFromDto(prepare.Ip, prepare.Info, "上传请求");

                        // 直接接受全部文件（PIN 由 ServerConfigV2 负责校验）
                        var accepted = prepare.Files.Keys.ToHashSet();
                        prepare.DecisionTx.TrySetResult(new PrepareUploadDecisionV2.Accept(accepted));
                        _logger.LogInformation(
                            "LocalSend 上传请求: {Alias}（{Count} 个文件）",
                            prepare.Info.Alias,
                            prepare.Files.Count);
                        break;

                    case ServerEventV2.FileUpload upload:
                        HandleFileUpload(upload);
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

            _ = WatchAsync(resultTx, targetPath, directory, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "准备保存 LocalSend 文件失败");
            upload.TargetTx.TrySetResult(new FileUploadTarget.PathTarget(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                new TaskCompletionSource<Result<Unit, string>>()));
        }
    }

    private async Task WatchAsync(
        TaskCompletionSource<Result<Unit, string>> resultTx,
        string targetPath,
        string directory,
        string fileName)
    {
        try
        {
            var result = await resultTx.Task.ConfigureAwait(false);
            if (!result.IsOk)
            {
                _logger.LogWarning("LocalSend 文件保存失败: {Path} ({Error})", targetPath, result.Error);
                return;
            }

            _logger.LogInformation("LocalSend 收到文件: {Path}", targetPath);

            var entity = new ReceiveHistoryEntity
            {
                SenderId = "localsend",
                SenderDeviceName = "LocalSend",
                PeerId = "localsend",
                PeerName = "LocalSend",
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
            _toastService?.Show("收到 LocalSend 文件", $"{fileName}（已保存到 {directory}）");
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
            return Core.Network.LocalNetworkHelper.GetLocalIPv4Addresses().FirstOrDefault()?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Stop()
    {
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
