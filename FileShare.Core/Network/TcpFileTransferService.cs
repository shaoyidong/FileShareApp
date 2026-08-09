using FileShare.Core.Common;
using FileShare.Core.Models;
using FileShare.Core.Network.Tls;
using FileShare.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileShare.Core.Network;

/// <summary>
/// TCP文件传输服务
/// </summary>
public class TcpFileTransferService : IDisposable
{
    private const int BUFFER_SIZE = 65536; // 64KB缓冲区
    private const int REQUEST_TIMEOUT_MS = 60000; // 请求超时时间（60秒）
    private const double PROGRESS_THRESHOLD = 0.5;
    private const long MaxFileSize = 100L * 1024 * 1024 * 1024; // 100GB
    private const int MaxConcurrentConnections = 50; // 最大并发连接数
    private const int MaxConnectionsPerIp = 10; // 单个IP最大并发连接数
    private const int ReadTimeoutMs = 30000; // 读取操作超时时间
    private const int DefaultGracefulShutdownTimeoutMs = 3000; // 优雅关闭默认等待时间
    private const int SendChannelCapacity = 4; // 发送管道容量（提供背压 + 读写重叠）

    // TLS 相关常量
    private const byte TlsHandshakeContentType = 0x16; // TLS 记录 ContentType：Handshake（ClientHello 首字节）
    private const byte TlsMajorVersion = 0x03; // TLS 主版本号（TLS1.0~1.3 第二字节均为 0x03）
    private const int TlsHandshakeTimeoutMs = 15000; // TLS 握手超时

    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly ConcurrentDictionary<string, FileTransferInfo> _incomingTransfers; // 接收的文件传输
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingTransferRequests; // 等待用户确认的传输请求
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferCancellationTokens; // 传输取消令牌
    private readonly ConcurrentDictionary<string, double> _lastProgressValues;// 上次进度值，用于优化事件触发
    private readonly ConcurrentDictionary<string, int> _ipConnectionCounts; // 每IP并发连接计数
    private readonly ConcurrentDictionary<string, (DateTime Time, long Bytes)> _lastRateSamples; // 速率采样（按传输ID）
    private readonly IPlatformDirectoryService _directoryService;
    private readonly ILogger<TcpFileTransferService> _logger;
    private int _activeTransfers; // 当前活跃传输数量（用于优雅关闭）
    private long _totalBytesSent; // 累计发送字节数
    private long _totalBytesReceived; // 累计接收字节数
    private bool _disposedValue;
    private volatile bool _isStopping;

    // TLS 可选加密传输：启用后对同样启用 TLS 的对端自动升级到 SslStream，证书指纹采用 TOFU（首次信任）策略。
    // 未启用（默认）保持裸 TCP，与旧版本完全兼容；既有协议帧读写逻辑不变。
    private readonly TlsOptions? _tlsOptions;
    private readonly FingerprintStore? _fingerprintStore;
    private readonly X509Certificate2? _localCertificate;
    private readonly bool _tlsEnabled;

    /// <summary>累计发送字节数</summary>
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    /// <summary>累计接收字节数</summary>
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    /// <summary>是否启用 TLS 加密传输</summary>
    public bool TlsEnabled => _tlsEnabled;

    /// <summary>
    /// 文件传输请求事件
    /// </summary>
    public event Action<FileTransferInfo>? OnTransferRequestSendAndReceive;

    /// <summary>
    /// 传输进度更新事件
    /// </summary>
    public event Action<FileTransferInfo>? OnTransferProgressUpdated;

    /// <summary>
    /// 传输完成事件
    /// </summary>
    public event Action<FileTransferInfo, string?>? OnTransferCompleted;

    public TcpFileTransferService(IPlatformDirectoryService directoryService, int port = 5237, ILogger<TcpFileTransferService>? logger = null, TlsOptions? tlsOptions = null, ILoggerFactory? loggerFactory = null)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _cts = new CancellationTokenSource();
        _connectionSemaphore = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);
        _incomingTransfers = new ConcurrentDictionary<string, FileTransferInfo>();
        _pendingTransferRequests = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        _transferCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
        _lastProgressValues = new ConcurrentDictionary<string, double>();
        _ipConnectionCounts = new ConcurrentDictionary<string, int>();
        _lastRateSamples = new ConcurrentDictionary<string, (DateTime, long)>();
        _directoryService = directoryService;
        _logger = logger ?? loggerFactory?.CreateLogger<TcpFileTransferService>() ?? NullLogger<TcpFileTransferService>.Instance;
        _tlsOptions = tlsOptions;

        if (tlsOptions is { Enabled: true })
        {
            try
            {
                // 指纹信任库与本地证书在构造期就绪，避免每次连接时重复生成/加载
                _fingerprintStore = new FingerprintStore(tlsOptions.FingerprintStorePath, loggerFactory?.CreateLogger<FingerprintStore>());
                var deviceIdHint = Guid.NewGuid().ToString(); // 证书 CN 仅为人类可读标识，校验依赖指纹而非 CN
                var certProvider = new SelfSignedCertificateProvider(tlsOptions, deviceIdHint, loggerFactory?.CreateLogger<SelfSignedCertificateProvider>());
                _localCertificate = certProvider.GetOrCreateCertificate();
                _tlsEnabled = true;
                _logger.LogInformation("TLS 加密传输已启用");
            }
            catch (Exception ex)
            {
                // TLS 初始化失败不应阻断文件传输服务启动，降级为裸 TCP
                _logger.LogError(ex, "TLS 初始化失败，降级为裸 TCP");
                _tlsEnabled = false;
            }
        }
    }

    /// <summary>
    /// 处理用户对传输请求的选择
    /// </summary>
    /// <param name="transferId">传输ID</param>
    /// <param name="accept">是否接受请求</param>
    /// <param name="savePath">文件保存路径</param>
    public void HandleTransferRequest(string transferId, bool accept, string? savePath = null)
    {
        // 保存路径信息到传输信息中
        if (!string.IsNullOrEmpty(savePath) && _incomingTransfers.TryGetValue(transferId, out var transferInfo))
        {
            transferInfo.SavePath = savePath;
        }

        if (_pendingTransferRequests.TryRemove(transferId, out var tcs))
        {
            tcs?.TrySetResult(accept);
        }
    }

    /// <summary>
    /// 取消传输
    /// </summary>
    /// <param name="transferId">传输ID</param>
    public void CancelTransfer(string transferId)
    {
        // 处理待处理的传输请求
        if (_pendingTransferRequests.TryRemove(transferId, out var tcs))
        {
            tcs?.TrySetResult(false);
        }

        // 获取并取消传输的取消令牌
        if (_transferCancellationTokens.TryGetValue(transferId, out var cts))
        {
            try
            {
                cts?.Cancel();
            }
            catch (Exception)
            {
                // 忽略取消时的异常
            }
        }
    }

    /// <summary>
    /// 启动文件传输服务
    /// </summary>
    public Task StartAsync()
    {
        _listener.Start();
        _ = Task.Run(() => AcceptConnectionsAsync(_cts.Token));
        _logger.LogInformation("文件传输服务已启动，端口 {Port}", _port);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止文件传输服务（优雅关闭）：停止接受新连接，等待活跃传输完成或超时后再强制取消
    /// </summary>
    public async Task StopAsync()
    {
        await StopAsync(TimeSpan.FromMilliseconds(DefaultGracefulShutdownTimeoutMs)).ConfigureAwait(false);
    }

    /// <summary>
    /// 停止文件传输服务（优雅关闭）
    /// </summary>
    /// <param name="gracefulTimeout">等待活跃传输完成的最长时间</param>
    public async Task StopAsync(TimeSpan gracefulTimeout)
    {
        _isStopping = true;

        // 停止接受新连接
        try
        {
            _listener.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止监听器时出错");
        }

        // 取消所有等待用户确认的请求
        foreach (var tcs in _pendingTransferRequests.Values)
        {
            tcs?.TrySetCanceled();
        }
        _pendingTransferRequests.Clear();

        // 等待活跃传输完成（有界等待）
        if (Interlocked.CompareExchange(ref _activeTransfers, 0, 0) > 0)
        {
            _logger.LogInformation("等待 {Count} 个活跃传输完成（最多 {Timeout}ms）", _activeTransfers, gracefulTimeout.TotalMilliseconds);
            var deadline = DateTime.UtcNow + gracefulTimeout;
            while (Interlocked.CompareExchange(ref _activeTransfers, 0, 0) > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        // 强制取消尚未完成的传输
        ForceCancelAllTransfers();
        _logger.LogInformation("文件传输服务已停止");
    }

    /// <summary>
    /// 立即停止文件传输服务（强制取消所有传输，不等待）
    /// </summary>
    public void Stop()
    {
        _isStopping = true;

        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
        }

        _cts.Cancel();

        // 取消所有等待中的请求
        foreach (var tcs in _pendingTransferRequests.Values)
        {
            tcs?.TrySetCanceled();
        }
        _pendingTransferRequests.Clear();

        ForceCancelAllTransfers();
    }

    /// <summary>
    /// 强制取消并清理所有传输资源
    /// </summary>
    private void ForceCancelAllTransfers()
    {
        foreach (var cts in _transferCancellationTokens.Values)
        {
            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 已释放，忽略
            }
        }
        _transferCancellationTokens.Clear();
        _incomingTransfers.Clear();
        _ipConnectionCounts.Clear();
    }

    #region 接收文件

    /// <summary>
    /// 接受连接请求（带全局与每IP并发限制）
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isStopping)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

                // 解析远端IP用于每IP限流
                string? remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();

                // 全局并发限制：如果已达到最大连接数，直接拒绝
                if (!_connectionSemaphore.Wait(0))
                {
                    try
                    {
                        client.Close();
                        client.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                    continue;
                }

                // 每IP并发限制：防止单一主机耗尽连接资源
                if (remoteIp != null && IncrementIpCount(remoteIp) > MaxConnectionsPerIp)
                {
                    DecrementIpCount(remoteIp);
                    _connectionSemaphore.Release();
                    try
                    {
                        client.Close();
                        client.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                    _logger.LogWarning("来自 {Ip} 的并发连接超过上限 {Limit}，已拒绝", remoteIp, MaxConnectionsPerIp);
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _connectionSemaphore.Release();
                        if (remoteIp != null)
                        {
                            DecrementIpCount(remoteIp);
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "接受连接失败");
        }
    }

    /// <summary>
    /// 处理客户端连接
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string? senderId = null;
        Interlocked.Increment(ref _activeTransfers);
        try
        {
            using (client)
            {
                var rawStream = client.GetStream();
                // 读取超时设置在底层 NetworkStream 上（SslStream 会转发；PrependedStream 不支持该属性，故仅在包装前设置）
                rawStream.ReadTimeout = ReadTimeoutMs;

                // TLS 可选升级：仅在 _tlsEnabled 时探测首字节是否为 TLS ClientHello（0x16 0x03）。
                // 非 TLS（含旧版本客户端的裸 TCP 请求）通过 PrependedStream 回放已读字节，协议帧读写逻辑不变。
                Stream stream = rawStream;
                SslStream? inboundSsl = null;
                if (_tlsEnabled)
                {
                    var upgraded = await TryUpgradeInboundStreamAsync(rawStream, cancellationToken).ConfigureAwait(false);
                    stream = upgraded.Stream;
                    inboundSsl = upgraded.SslStream;
                }

                try
                {
                    bool tlsPeerValidated = !_tlsEnabled; // 未启用 TLS 时无需校验；启用时首条请求后做一次 TOFU
                    while (!cancellationToken.IsCancellationRequested && client.Connected)
                    {
                        try
                        {
                            // 使用读取超时机制替代 DataAvailable 轮询
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            readCts.CancelAfter(ReadTimeoutMs);

                            var headerBytes = await ReadBytesAsync(stream, 4, readCts.Token).ConfigureAwait(false);
                            if (headerBytes.Length == 0) break;

                            var headerLength = BitConverter.ToInt32(headerBytes, 0);
                            if (headerLength <= 0 || headerLength > 10 * 1024 * 1024) // 请求体最大10MB
                            {
                                _logger.LogWarning("无效的请求头长度: {Length}", headerLength);
                                break;
                            }

                            var requestBytes = await ReadBytesAsync(stream, headerLength, readCts.Token).ConfigureAwait(false);
                            if (requestBytes.Length == 0) break;

                            var requestJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                            var request = JsonSerializer.Deserialize<TransferRequest>(requestJson, SourceGenerationContext.Default.TransferRequest);

                            if (request != null)
                            {
                                // 输入校验
                                if (!ValidateRequest(request))
                                {
                                    await SendErrorResponseAsync(request.TransferId, stream, "无效的请求参数", cancellationToken).ConfigureAwait(false);
                                    break;
                                }

                                senderId = request.SenderId;

                                // TLS 后置 TOFU 校验：握手时未知对端身份，读出首条请求拿到 SenderId 后再校验其证书指纹。
                                // 仅在首条请求执行一次；指纹不一致视为中间人攻击，中止连接（此时仅泄露文件元数据，未交换文件内容）。
                                if (!tlsPeerValidated && inboundSsl != null && !string.IsNullOrEmpty(senderId))
                                {
                                    if (!ValidateInboundPeerFingerprint(inboundSsl, senderId))
                                    {
                                        await SendErrorResponseAsync(request.TransferId, stream, "证书校验失败", cancellationToken).ConfigureAwait(false);
                                        break;
                                    }
                                    tlsPeerValidated = true;
                                }

                                switch (request.Type)
                                {
                                    case TransferRequestType.SendFileRequest:
                                        await HandleSendFileRequest(stream, request, cancellationToken).ConfigureAwait(false);
                                        break;
                                    case TransferRequestType.SendFileData:
                                        await HandleFileData(stream, request, cancellationToken).ConfigureAwait(false);
                                        break;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // 读取超时，关闭连接
                            _logger.LogDebug("读取超时，关闭连接");
                            break;
                        }
                        catch (IOException ex) when (IsConnectionReset(ex))
                        {
                            _logger.LogDebug("客户端连接被中止: {Message}", ex.Message);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogDebug("处理请求被取消");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理请求失败");

                            try
                            {
                                var response = new TransferResponse
                                {
                                    TransferId = "unknown",
                                    Accepted = false,
                                    Message = "处理请求失败: " + ex.Message
                                };
                                await SendResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // 忽略发送错误响应时的异常
                            }
                            // 继续处理下一个请求，而不是关闭连接
                        }
                    }
                }
                finally
                {
                    // 包装流（SslStream/PrependedStream）会释放底层 NetworkStream；裸 TCP 时直接释放 rawStream
                    if (!ReferenceEquals(stream, rawStream))
                        await stream.DisposeAsync().ConfigureAwait(false);
                    else
                        await rawStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理客户端连接失败");
        }
        finally
        {
            Interlocked.Decrement(ref _activeTransfers);
            // 连接关闭时，根据发送方ID或IP清理接收传输数据
            if (!string.IsNullOrEmpty(senderId))
            {
                // 清理所有来自该发送方的接收传输数据
                var senderTransfers = _incomingTransfers.Where(t => t.Value.SenderId == senderId);
                foreach (var transfer in senderTransfers)
                {
                    _incomingTransfers.TryRemove(transfer.Key, out _);
                    transfer.Value.Status = TransferStatus.Failed;
                    OnTransferCompleted?.Invoke(transfer.Value, "连接已关闭");
                }
            }
        }
    }

    /// <summary>
    /// 校验传输请求
    /// </summary>
    private bool ValidateRequest(TransferRequest request)
    {
        if (request == null || string.IsNullOrEmpty(request.TransferId)
            || string.IsNullOrEmpty(request.SenderId) || string.IsNullOrEmpty(request.ReceiverId))
            return false;

        if (request.FileSize < 0 || request.FileSize > MaxFileSize)
            return false;

        if (string.IsNullOrEmpty(request.FileName) || request.FileName.Length > 255)
            return false;
        return true;
    }

    /// <summary>
    /// 判断是否为连接重置异常
    /// </summary>
    private static bool IsConnectionReset(IOException ex)
    {
        return ex.InnerException is SocketException socketEx &&
               (socketEx.SocketErrorCode == SocketError.ConnectionReset ||
                socketEx.SocketErrorCode == SocketError.ConnectionAborted ||
                socketEx.SocketErrorCode == SocketError.Shutdown);
    }

    #region TLS 可选加密升级

    /// <summary>
    /// 接收方在连接建立后探测首字节，判断是否为 TLS ClientHello，是则升级到 SslStream，否则保持裸 TCP。
    /// <para>探测策略：读取首字节，若为 0x16（TLS Handshake ContentType）再读一字节确认 0x03（TLS 主版本）。
    /// 双字节匹配才认定 TLS，避免单字节 0x16 与裸 TCP 长度头 LSB 偶然碰撞（如 ~278 字节请求）造成误判。</para>
    /// <para>已读字节通过 <see cref="PrependedStream"/> 回放：TLS 路径下 SslStream 从 PrependedStream 读取完整 ClientHello；
    /// 裸 TCP 路径下后续 ReadBytesAsync 读到的 4 字节长度头包含回放字节。</para>
    /// </summary>
    /// <returns>(Stream, SslStream?)：升级后的流（SslStream 或回放用的 PrependedStream）与 SslStream 引用（裸 TCP 时为 null）。</returns>
    private async Task<(Stream Stream, SslStream? SslStream)> TryUpgradeInboundStreamAsync(Stream rawStream, CancellationToken cancellationToken)
    {
        // 读首字节
        var firstByte = new byte[1];
        int n1 = await ReadOneByteAsync(rawStream, firstByte, cancellationToken).ConfigureAwait(false);
        if (n1 == 0)
        {
            // 对端立即断开；返回回放空前缀的包装流以保持统一的释放路径
            return (new PrependedStream(rawStream, Array.Empty<byte>()), null);
        }

        if (firstByte[0] != TlsHandshakeContentType)
        {
            // 裸 TCP：回放首字节
            return (new PrependedStream(rawStream, firstByte), null);
        }

        // 首字节为 0x16，读第二字节确认 TLS 版本
        var peek = new byte[2];
        peek[0] = firstByte[0];
        int n2 = await ReadOneByteAsync(rawStream, peek.AsMemory(1), cancellationToken).ConfigureAwait(false);
        if (n2 == 0 || peek[1] != TlsMajorVersion)
        {
            // 单字节 0x16 但非 TLS（裸 TCP 请求 LSB 恰为 0x16）：回放已读字节，按裸 TCP 处理
            return (new PrependedStream(rawStream, peek[..(1 + n2)]), null);
        }

        // 确认 TLS ClientHello：升级到 SslStream。
        // 注意：一旦确认是 TLS，握手失败不可降级回裸 TCP（SslStream 可能已消费底层流字节，回放会错位），
        // 故让异常向上传播，由 HandleClientAsync 统一捕获并关闭连接。
        var prepended = new PrependedStream(rawStream, peek);
        var ssl = new SslStream(prepended, leaveInnerStreamOpen: false);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(TlsHandshakeTimeoutMs);

        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = _localCertificate!,
            // 双向认证：要求客户端出示证书；握手期暂不校验（未知对端身份），读出首条请求拿到 SenderId 后做 TOFU。
            // 限定 TLS 1.2：TLS 1.3 下客户端证书在握手后阶段交换，部分平台 RemoteCertificate 读取时序不稳定，影响 TOFU。
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        };

        await ssl.AuthenticateAsServerAsync(options, handshakeCts.Token).ConfigureAwait(false);
        _logger.LogDebug("入站连接已升级到 TLS");
        return (ssl, ssl);
    }

    /// <summary>
    /// 发送方在 TCP 连接建立后，若本端与对端均启用 TLS，升级到 SslStream（双向认证 + TOFU 校验对端证书）。
    /// </summary>
    private async Task<Stream> UpgradeOutboundStreamToTlsAsync(Stream rawStream, string remoteDeviceId, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false,
            (_, cert, _, _) => ValidateRemoteCertificate(remoteDeviceId, cert));

        var clientCerts = new X509CertificateCollection { _localCertificate! };

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(TlsHandshakeTimeoutMs);

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = remoteDeviceId, // 用于 SNI 与日志，不用于证书校验（自签名证书改用 TOFU）
            ClientCertificates = clientCerts,
            EnabledSslProtocols = SslProtocols.Tls12, // 与服务端一致，避免 TLS 1.3 客户端证书时序问题
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };

        await ssl.AuthenticateAsClientAsync(options, handshakeCts.Token).ConfigureAwait(false);
        _logger.LogDebug("出站连接已升级到 TLS（目标设备 {DeviceId}）", remoteDeviceId);
        return ssl;
    }

    /// <summary>
    /// 发送方校验接收方证书：按 TOFU 策略记录/比对指纹（自签名证书必然产生 ChainErrors，故忽略 SslPolicyErrors）。
    /// </summary>
    private bool ValidateRemoteCertificate(string remoteDeviceId, X509Certificate? cert)
    {
        // SslStream 实际提供 X509Certificate2 实例；不释放 cert2（由 SslStream 持有生命周期）
        if (cert is not X509Certificate2 cert2)
        {
            _logger.LogWarning("设备 {DeviceId} 未提供有效的 TLS 证书，拒绝连接", remoteDeviceId);
            return false;
        }

        try
        {
            return _fingerprintStore!.ValidateAndStore(remoteDeviceId, cert2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "校验远端证书失败（设备 {DeviceId}）", remoteDeviceId);
            return false;
        }
    }

    /// <summary>
    /// 接收方后置 TOFU 校验：握手后从 SslStream.RemoteCertificate 取对端证书，按 SenderId 校验指纹。
    /// </summary>
    private bool ValidateInboundPeerFingerprint(SslStream ssl, string senderId)
    {
        // SslStream.RemoteCertificate 运行时为 X509Certificate2；不释放（由 SslStream 持有）
        if (ssl.RemoteCertificate is not X509Certificate2 cert2)
        {
            _logger.LogWarning("设备 {DeviceId} 未提供客户端 TLS 证书，拒绝连接", senderId);
            return false;
        }

        try
        {
            return _fingerprintStore!.ValidateAndStore(senderId, cert2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "后置校验对端证书失败（设备 {DeviceId}）", senderId);
            return false;
        }
    }

    /// <summary>
    /// 读取单字节（带取消），返回实际读取字节数（0 表示 EOF）。
    /// </summary>
    private static async Task<int> ReadOneByteAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        return await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取单字节到指定 Memory（带取消），返回实际读取字节数（0 表示 EOF）。
    /// </summary>
    private static async Task<int> ReadOneByteAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    /// <summary>
    /// 处理发送文件请求
    /// </summary>
    private async Task HandleSendFileRequest(Stream stream, TransferRequest request, CancellationToken cancellationToken = default)
    {
        var transferInfo = new FileTransferInfo
        {
            TransferId = request.TransferId,
            FileName = request.FileName,
            FileSize = request.FileSize,
            SenderId = request.SenderId,
            ReceiverId = request.ReceiverId,
            Status = TransferStatus.Pending,
            Direction = TransferDirection.Receive,
        };

        // 创建TaskCompletionSource来等待用户选择
        var tcs = new TaskCompletionSource<bool>();
        _pendingTransferRequests[transferInfo.TransferId] = tcs;

        try
        {
            _incomingTransfers[transferInfo.TransferId] = transferInfo;
            // 触发传输请求事件
            OnTransferRequestSendAndReceive?.Invoke(transferInfo);

            // 等待用户选择，设置超时
            var timeoutTask = Task.Delay(REQUEST_TIMEOUT_MS, cancellationToken);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            bool accepted;
            if (completedTask == timeoutTask)
            {
                accepted = false;
                transferInfo.Status = TransferStatus.Cancelled;
                OnTransferCompleted?.Invoke(transferInfo, "超时未选择");
            }
            else
            {
                accepted = await tcs.Task.ConfigureAwait(false);
                if (!accepted)
                {
                    transferInfo.Status = TransferStatus.Cancelled;
                    OnTransferCompleted?.Invoke(transferInfo, "拒绝");
                }
                // 接受请求时保持Pending状态，实际传输开始时会在HandleFileData中设置为Transferring
            }

            if (!accepted)
            {
                _incomingTransfers.TryRemove(transferInfo.TransferId, out _);
            }

            // 发送响应
            var response = new TransferResponse
            {
                TransferId = transferInfo.TransferId,
                Accepted = accepted,
                Message = accepted ? "准备接收文件" : "文件传输请求已被拒绝"
            };

            await SendResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            transferInfo.Status = TransferStatus.Failed;
            OnTransferCompleted?.Invoke(transferInfo, $"异常: {ex.Message}");
        }
        finally
        {
            // 清理等待任务
            _pendingTransferRequests.TryRemove(transferInfo.TransferId, out _);
        }
    }

    /// <summary>
    /// 处理文件数据
    /// </summary>
    private async Task HandleFileData(Stream stream, TransferRequest request, CancellationToken cancellationToken = default)
    {
        if (!_incomingTransfers.TryGetValue(request.TransferId, out var transferInfo))
        {
            await SendErrorResponseAsync(request.TransferId, stream, "传输不存在", cancellationToken).ConfigureAwait(false);
            return;
        }

        var savePath = transferInfo.SavePath;
        if (string.IsNullOrEmpty(savePath))
        {
            savePath = FileTypeHelper.GetDirectoryByFileType(request.FileName, _directoryService);
            transferInfo.SavePath = savePath;
        }
        var tempFilePath = Path.Combine(savePath, request.FileName);

            // 这个操作创建了一个新的 CancellationTokenSource，
            // 它会在两个条件之一发生时取消：
            // 1. 外部 cancellationToken 被取消时
            // 2. 自己调用 cts.Cancel() 时
            // 创建取消令牌源并保存到字典中
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            _transferCancellationTokens[request.TransferId] = cts;

            transferInfo.Status = TransferStatus.Transferring;
            OnTransferProgressUpdated?.Invoke(transferInfo);

            if (!Directory.Exists(savePath))
            {
                Directory.CreateDirectory(savePath);
            }

            var totalBytesRead = 0L;
            using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
            {
                var buffer = new byte[BUFFER_SIZE];

                while (totalBytesRead < request.FileSize && !cts.Token.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
                    if (bytesRead == 0) break;

                    await fileStream.WriteAsync(buffer, 0, bytesRead, cts.Token).ConfigureAwait(false);
                    totalBytesRead += bytesRead;
                    Interlocked.Add(ref _totalBytesReceived, bytesRead);

                    UpdateTransferProgress(transferInfo, totalBytesRead);
                }
            }

            if (cts.Token.IsCancellationRequested)
            {
                await OnReceiverCancelled(stream, transferInfo, tempFilePath).ConfigureAwait(false);
                return;
            }

                // 检查传输进度是否达到100%
            if (totalBytesRead == request.FileSize)
            {
                // 如果有校验和，进行验证
                if (!string.IsNullOrEmpty(request.Checksum))
                {
                    var actualChecksum = ComputeFileChecksum(tempFilePath);
                    if (!string.Equals(actualChecksum, request.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        transferInfo.Status = TransferStatus.Failed;
                        OnTransferCompleted?.Invoke(transferInfo, "文件校验失败");

                        if (File.Exists(tempFilePath))
                        {
                            File.Delete(tempFilePath);
                        }

                        await SendErrorResponseAsync(transferInfo.TransferId, stream, "文件校验失败", cts.Token).ConfigureAwait(false);
                        return;
                    }
                }

                transferInfo.Status = TransferStatus.Completed;
                OnTransferCompleted?.Invoke(transferInfo, null);

                    // 发送完成响应
                var response = new TransferResponse
                {
                    TransferId = transferInfo.TransferId,
                    Accepted = true,
                    Message = "文件接收完成"
                };
                await SendResponseAsync(stream, response, cts.Token).ConfigureAwait(false);
            }
            else
            {
                    // 传输中断，没到100%
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo, "传输中断");

                    // 删除临时文件
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }

                try
                {
                    await SendErrorResponseAsync(transferInfo.TransferId, stream, "文件接收失败: 传输中断", cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                        // 忽略发送错误响应时的异常
                }
            }
        }
        catch (OperationCanceledException)
        {
            await OnReceiverCancelled(stream, transferInfo, tempFilePath).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            transferInfo.Status = TransferStatus.Failed;
            OnTransferCompleted?.Invoke(transferInfo, $"异常: {ex.Message}");

            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }

            try
            {
                await SendErrorResponseAsync(transferInfo.TransferId, stream, "文件接收失败: " + ex.Message, cts.Token).ConfigureAwait(false);

            }
            catch (Exception)
            {
                    // 忽略发送错误响应时的异常
            }
        }
        finally
        {
            _incomingTransfers.TryRemove(transferInfo.TransferId, out _);
            _transferCancellationTokens.TryRemove(request.TransferId, out _);
            _lastProgressValues.TryRemove(transferInfo.TransferId, out _);
            _lastRateSamples.TryRemove(transferInfo.TransferId, out _);
            cts?.Dispose();
        }
    }

    /// <summary>
    /// 计算文件的SHA256校验和
    /// </summary>
    private static string ComputeFileChecksum(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, System.IO.FileShare.Read);
        var hashBytes = sha256.ComputeHash(fileStream);
        return Convert.ToHexString(hashBytes);
    }

    private async Task OnReceiverCancelled(Stream stream, FileTransferInfo transferInfo, string tempFilePath)
    {
        // 传输被取消
        transferInfo.Status = TransferStatus.Cancelled;
        OnTransferCompleted?.Invoke(transferInfo, "传输被接收方取消");

        // 删除临时文件
        if (File.Exists(tempFilePath))
        {
            File.Delete(tempFilePath);
        }

        // 发送取消响应给发送方
        try
        {
            await SendErrorResponseAsync(transferInfo.TransferId, stream, "传输被接收方取消", _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略发送取消响应的异常
        }
    }

    /// <summary>
    /// 发送响应
    /// </summary>
    private async Task SendResponseAsync(Stream stream, TransferResponse response, CancellationToken cancellationToken = default)
    {
        var responseJson = JsonSerializer.Serialize(response, SourceGenerationContext.Default.TransferResponse);
        var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
        var headerBytes = BitConverter.GetBytes(responseBytes.Length);

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送错误响应
    /// </summary>
    private async Task SendErrorResponseAsync(string transferId,Stream stream, string message, CancellationToken cancellationToken)
    {
        var response = new TransferResponse
        {
            TransferId = transferId,
            Accepted = false,
            Message = message
        };
        await SendResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region 发送文件

    /// <summary>
    /// 发送文件到指定设备
    /// </summary>
    public async Task<bool> SendFileAsync(string filePath, DeviceInfo targetDevice, string senderId)
    {
        string? transferId = null;
        CancellationTokenSource? userCts = null;
        CancellationTokenSource? timeoutCts = null;
        FileTransferInfo? transferInfo = null;
        Stream? stream = null;
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("文件不存在: {Path}", filePath);
                return false;
            }

            var fileInfo = new FileInfo(filePath);
            transferId = Guid.NewGuid().ToString();

            // 计算文件校验和
            var checksum = ComputeFileChecksum(filePath);

            transferInfo = new FileTransferInfo
            {
                TransferId = transferId,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                SenderId = senderId,
                ReceiverId = targetDevice.DeviceId,
                Status = TransferStatus.Pending,
                Direction = TransferDirection.Send,
            };

            OnTransferRequestSendAndReceive?.Invoke(transferInfo);

            userCts = new CancellationTokenSource();
            _transferCancellationTokens[transferId] = userCts;
            timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(REQUEST_TIMEOUT_MS));
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, userCts.Token, timeoutCts.Token))
            {
                using (var client = new TcpClient())
                {
                    // 连接到目标设备，添加超时
                    await client.ConnectAsync(targetDevice.IpAddress, targetDevice.Port, cts.Token).ConfigureAwait(false);
                    using (stream = client.GetStream())
                    {
                        // 双方均启用 TLS 时升级到 SslStream（双向认证 + TOFU 校验对端证书）；
                        // 否则保持裸 TCP，向后兼容未启用 TLS 的旧版本对端。
                        if (_tlsEnabled && targetDevice.SupportsTls)
                        {
                            stream = await UpgradeOutboundStreamToTlsAsync(stream, targetDevice.DeviceId, cts.Token).ConfigureAwait(false);
                        }

                        // 发送文件请求
                        var request = new TransferRequest
                        {
                            Type = TransferRequestType.SendFileRequest,
                            TransferId = transferId,
                            FileName = fileInfo.Name,
                            FileSize = fileInfo.Length,
                            SenderId = senderId,
                            ReceiverId = targetDevice.DeviceId,
                            Checksum = checksum
                        };

                        await SendRequestAsync(stream, request, cts.Token).ConfigureAwait(false);

                        // 接收响应
                        var response = await ReceiveResponseAsync(stream, cts.Token).ConfigureAwait(false);

                        if (response?.Accepted ?? false)
                        {
                            // 发送文件数据，增加文件传输的超时时间
                            // 初始设置超时，后续会根据进度更新重置
                            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(REQUEST_TIMEOUT_MS));

                            var (dataSent, preReadResponse) = await SendFileDataAsync(stream, filePath, transferInfo, cts.Token, timeoutCts).ConfigureAwait(false);
                            //发送流程未被接收方取消
                            if (dataSent)
                            {
                                if (cts.IsCancellationRequested)
                                {
                                    //发送方自行取消
                                    if (userCts.IsCancellationRequested)
                                    {
                                        await OnSenderCancelled(stream, transferInfo, false).ConfigureAwait(false);
                                    }
                                    //发送方超时自行取消
                                    if (timeoutCts.IsCancellationRequested)
                                    {
                                        await OnSenderCancelled(stream, transferInfo, true).ConfigureAwait(false);
                                    }
                                    return false;
                                }
                                else
                                {
                                    // 接收完成响应：优先使用监听任务提前读到的响应，避免重复读取被阻塞
                                    response = preReadResponse ?? await ReceiveResponseAsync(stream, cts.Token).ConfigureAwait(false);

                                    if (response?.Accepted ?? false)
                                    {
                                        transferInfo.Status = TransferStatus.Completed;
                                        OnTransferCompleted?.Invoke(transferInfo, null);
                                        return true;
                                    }
                                    else
                                    {
                                        _logger.LogWarning("文件传输被接收方拒绝: {Message}", response?.Message);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("文件请求被接收方拒绝: {Message}", response?.Message);
                        }
                    }
                }
            }

            transferInfo.Status = TransferStatus.Cancelled;
            OnTransferCompleted?.Invoke(transferInfo, "传输被接收方拒绝");
            return false;
        }
        catch (OperationCanceledException)
        {
            //发送方自行取消
            if (userCts?.IsCancellationRequested ?? false)
            {
                if (transferInfo != null && stream != null)
                {
                    await OnSenderCancelled(stream, transferInfo, false).ConfigureAwait(false);
                }
            }
            //发送方超时自行取消
            if (timeoutCts?.IsCancellationRequested ?? false)
            {
                if (transferInfo != null && stream != null)
                {
                    await OnSenderCancelled(stream, transferInfo, true).ConfigureAwait(false);
                }
            }
            return false;
        }
        catch (IOException ex) when (IsConnectionReset(ex))
        {
            _logger.LogWarning("连接被接收方中断: {Message}", ex.Message);
            if (transferInfo != null)
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo, "连接被接收方中断");
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件传输失败");
            if (transferInfo != null)
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo, $"异常: " + ex.Message);
            }
            return false;
        }
        finally
        {
            // 清理资源
            if (transferId != null)
            {
                _transferCancellationTokens.TryRemove(transferId, out _);
                _lastProgressValues.TryRemove(transferId, out _);
                userCts?.Dispose();
                timeoutCts?.Dispose();
                stream?.Dispose();
            }
        }
    }

    private async Task OnSenderCancelled(Stream stream, FileTransferInfo transferInfo, bool isTimeout = false)
    {
        transferInfo.Status = TransferStatus.Cancelled;
        OnTransferCompleted?.Invoke(transferInfo, isTimeout ? "传输超时" : "传输被发送方取消");
    }

    /// <summary>
    /// 发送文件数据（基于 Channel&lt;T&gt; 的生产者-消费者模型 + 并发响应监听）
    /// <para>生产者：从文件读取数据块写入有界 Channel（满时等待 → 天然背压）。</para>
    /// <para>消费者（主流程）：从 Channel 读出并写入网络。</para>
    /// <para>监听任务：与写入并发地读取接收方响应（TCP 全双工），替换原 50ms 轮询。</para>
    /// <para>关键设计：消费者完成后【等待】监听任务自然完成，而非取消它。
    /// 接收方在收完数据并校验后发送完成响应，监听任务读取该响应并缓存供上层使用；
    /// 这避免了"取消正在读取的 ReadAsync 导致已消费字节丢失、损坏流"的竞争问题，
    /// 且不依赖 DataAvailable（因此兼容 SslStream，为 TLS 加密传输铺路）。</para>
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="filePath"></param>
    /// <param name="transferInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="timeoutCts">超时取消令牌源，用于在进度更新时重置</param>
    /// <returns>(DataSent, PreReadResponse)：DataSent=true 表示数据已发完或自行取消，false 表示接收方取消；
    /// PreReadResponse 为监听任务读到的完成响应（避免上层重复读取被阻塞），null 表示需上层自行读取或已取消。</returns>
    private async Task<(bool DataSent, TransferResponse? PreReadResponse)> SendFileDataAsync(Stream stream, string filePath, FileTransferInfo transferInfo, CancellationToken cancellationToken = default, CancellationTokenSource? timeoutCts = null)
    {
        transferInfo.Status = TransferStatus.Transferring;
        // 触发传输进度事件，通知UI状态变化
        OnTransferProgressUpdated?.Invoke(transferInfo);

        // 发送文件数据请求头
        var request = new TransferRequest
        {
            Type = TransferRequestType.SendFileData,
            TransferId = transferInfo.TransferId,
            FileName = transferInfo.FileName,
            FileSize = transferInfo.FileSize,
            SenderId = transferInfo.SenderId,
            ReceiverId = transferInfo.ReceiverId
        };
        await SendRequestAsync(stream, request, cancellationToken).ConfigureAwait(false);

        // 有界 Channel 提供背压：生产者读文件，消费者写网络，容量4使读写可重叠且不爆内存
        var channel = Channel.CreateBounded<(byte[] Buffer, int Length)>(
            new BoundedChannelOptions(SendChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        // 链接令牌：监听任务检测到接收方取消时停止生产/消费；上层取消/超时也会经此传播
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 监听任务：并发读取接收方响应（TCP/SslStream 全双工，与 WriteAsync 不冲突）
        // 原实现每轮用 50ms 超时轮询，此处改为常驻阻塞读，响应更快且无忙等。
        // 读到取消响应 → 标记并停止；读到完成响应 → 缓存供上层使用，避免上层重复读取被阻塞。
        var receiverCancelled = false;
        TransferResponse? preReadResponse = null;
        var monitorTask = Task.Run(async () =>
        {
            try
            {
                var response = await ReceiveResponseAsync(stream, linkedCts.Token).ConfigureAwait(false);
                if (response != null && response.Accepted == false)
                {
                    // 接收方取消：触发用户取消令牌，停止生产/消费
                    receiverCancelled = true;
                    if (_transferCancellationTokens.TryGetValue(transferInfo.TransferId, out var userCts))
                    {
                        userCts?.Cancel();
                                //cancellationToken.ThrowIfCancellationRequested();
                    }
                    linkedCts.Cancel();
                }
                else if (response != null)
                {
                    // 非取消响应（即完成响应）：缓存供上层使用
                    preReadResponse = response;
                }
            }
            catch (OperationCanceledException)
            {
                // 上层取消/超时传播，未读到响应（或处于错误路径，上层不再读取流）
            }
            catch (Exception)
            {
                // 监听异常（如连接重置）不在此处中断，由消费者的 WriteAsync 抛出后统一处理
            }
        });

        // 生产者：从文件读取数据块写入 Channel，缓冲区从 ArrayPool 租用
        var producerTask = Task.Run(async () =>
        {
            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, System.IO.FileShare.Read, BUFFER_SIZE, useAsync: true);
                while (true)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                    int bytesRead;
                    try
                    {
                        bytesRead = await fileStream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }

                    try
                    {
                        await channel.Writer.WriteAsync((buffer, bytesRead), linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 消费者停止，归还未被消费的缓冲区
                        ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                return;
            }
            channel.Writer.TryComplete();
        });

        // 消费者（主流程）：从 Channel 读出并写入网络
        var totalBytesRead = 0L;
        try
        {
            await foreach (var (buffer, length) in channel.Reader.ReadAllAsync(linkedCts.Token).ConfigureAwait(false))
            {
                try
                {
                    await stream.WriteAsync(buffer, 0, length, linkedCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    throw;
                }
                finally
                {
                    // 无论写入成功或取消，都归还缓冲区
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                totalBytesRead += length;
                Interlocked.Add(ref _totalBytesSent, length);

                // 更新进度，传递timeoutCts以便在进度更新时重置超时
                UpdateTransferProgress(transferInfo, totalBytesRead, timeoutCts);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消（本地取消或接收方取消），由返回值与上层判断来源
        }
        catch (Exception ex) when (ex is not ChannelClosedException) // 捕获所有其他异常
        {
            // 关键修复：网络异常导致消费者失败，立即取消所有关联任务
            _logger.LogError(ex, "消费者写入网络失败，正在取消整个传输");
            linkedCts.Cancel(); // 触发生产者 WriteAsync 取消，触发监听任务取消
            throw; // 继续向上抛出异常
        }
        catch (ChannelClosedException ex) when (ex.InnerException != null)
        {
            // 生产者异常，同样取消监听任务（防止泄漏）
            linkedCts.Cancel();
            // 生产者异常导致 Channel 关闭，向上抛出原始异常
            throw ex.InnerException;
        }

        // 等待生产者完成
        try
        {
            await producerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取文件 {Path} 失败", filePath);
        }

        // 等待监听任务【自然完成】：接收方在收完数据并校验后发送完成响应，监听任务会读到它。
        // 不主动取消监听，避免取消正在进行的 ReadAsync 导致已消费字节丢失、损坏流。
        // 正常完成路径：监听读到完成响应 → preReadResponse 由上层使用（等价于上层原本的 ReceiveResponseAsync）。
        // 错误/超时路径：linkedCts 经上层取消传播，监听抛 OCE 退出（此时上层处于取消分支，不再读取流）。
        try
        {
            await monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 监听任务因取消而退出，正常
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "监听任务异常退出");
        }

        return (!receiverCancelled, preReadResponse);
    }

    /// <summary>
    /// 发送请求
    /// </summary>
    private async Task SendRequestAsync(Stream stream, TransferRequest request, CancellationToken cancellationToken = default)
    {
        var requestJson = JsonSerializer.Serialize(request, SourceGenerationContext.Default.TransferRequest);
        var requestBytes = System.Text.Encoding.UTF8.GetBytes(requestJson);
        var headerBytes = BitConverter.GetBytes(requestBytes.Length);

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 接收响应
    /// </summary>
    private async Task<TransferResponse?> ReceiveResponseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var headerBytes = await ReadBytesAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        var headerLength = BitConverter.ToInt32(headerBytes, 0);
        var responseBytes = await ReadBytesAsync(stream, headerLength, cancellationToken).ConfigureAwait(false);
        var responseJson = System.Text.Encoding.UTF8.GetString(responseBytes);

        return JsonSerializer.Deserialize<TransferResponse>(responseJson, SourceGenerationContext.Default.TransferResponse);
    }
    #endregion

    /// <summary>
    /// 读取指定长度的字节
    /// </summary>
    private async Task<byte[]> ReadBytesAsync(Stream stream, int length, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[length];
        var totalBytesRead = 0;

        while (totalBytesRead < length)
        {
            var bytesRead = await stream.ReadAsync(buffer, totalBytesRead, length - totalBytesRead, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                throw new EndOfStreamException();

            totalBytesRead += bytesRead;
        }

        return buffer;
    }

    private void UpdateTransferProgress(FileTransferInfo transferInfo, long totalBytesRead, CancellationTokenSource? timeoutCts = null)
    {
        // 更新进度
        transferInfo.TransferredSize = totalBytesRead;

        // 计算瞬时传输速率（基于上次采样）
        var now = DateTime.UtcNow;
        if (_lastRateSamples.TryGetValue(transferInfo.TransferId, out var sample))
        {
            var elapsedSec = (now - sample.Time).TotalSeconds;
            if (elapsedSec > 0)
            {
                transferInfo.TransferRateBytesPerSec = (long)((totalBytesRead - sample.Bytes) / elapsedSec);
            }
        }
        else
        {
            transferInfo.StartTime = now;
        }
        _lastRateSamples[transferInfo.TransferId] = (now, totalBytesRead);

        // 计算当前进度百分比
        double currentProgress = transferInfo.ProgressPercentage;

        // 获取上次进度值，如果不存在则默认为-1
        double lastProgress = -1;
        _lastProgressValues.TryGetValue(transferInfo.TransferId, out lastProgress);

        // 只有当进度变化超过0.5%时才触发事件
        if (Math.Abs(currentProgress - lastProgress) >= PROGRESS_THRESHOLD || lastProgress < 0)
        {
            OnTransferProgressUpdated?.Invoke(transferInfo);
            _lastProgressValues[transferInfo.TransferId] = currentProgress;

            // 重置超时时间，只要有进度更新就不触发超时
            timeoutCts?.CancelAfter(TimeSpan.FromMilliseconds(REQUEST_TIMEOUT_MS));
        }
    }

    /// <summary>
    /// 原子递增某IP的连接计数，返回递增后的值
    /// </summary>
    private int IncrementIpCount(string ip)
    {
        return _ipConnectionCounts.AddOrUpdate(ip, 1, (_, c) => c + 1);
    }

    /// <summary>
    /// 原子递减某IP的连接计数（不低于0）
    /// </summary>
    private void DecrementIpCount(string ip)
    {
        _ipConnectionCounts.AddOrUpdate(ip, 0, (_, c) => c > 0 ? c - 1 : 0);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // TODO: 释放托管状态(托管对象)
                Stop();
                _cts.Dispose();
                _connectionSemaphore.Dispose();
            }

            // TODO: 释放未托管的资源(未托管的对象)并重写终结器
            // TODO: 将大型字段设置为 null
            _disposedValue = true;
        }
    }

    // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
    // ~TcpFileTransferService()
    // {
    //     // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 传输请求类型
/// </summary>
public enum TransferRequestType
{
    SendFileRequest,
    SendFileData,
}

/// <summary>
/// 传输请求
/// </summary>
public class TransferRequest
{
    public TransferRequestType Type { get; set; }
    public required string TransferId { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public required string SenderId { get; set; }
    public required string ReceiverId { get; set; }
    public string? Checksum { get; set; }
}

/// <summary>
/// 传输响应
/// </summary>
public class TransferResponse
{
    public required string TransferId { get; set; }
    public bool Accepted { get; set; }
    public string? Message { get; set; }
}
