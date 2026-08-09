using FileShare.Core.Common;
using FileShare.Core.Models;
using FileShare.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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

    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly ConcurrentDictionary<string, FileTransferInfo> _incomingTransfers; // 接收的文件传输
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingTransferRequests; // 等待用户确认的传输请求
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferCancellationTokens; // 传输取消令牌
    private readonly ConcurrentDictionary<string, double> _lastProgressValues;// 上次进度值，用于优化事件触发
    private readonly ConcurrentDictionary<string, int> _ipConnectionCounts; // 每IP并发连接计数
    private readonly IPlatformDirectoryService _directoryService;
    private readonly ILogger<TcpFileTransferService> _logger;
    private int _activeTransfers; // 当前活跃传输数量（用于优雅关闭）
    private bool _disposedValue;
    private volatile bool _isStopping;

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

    public TcpFileTransferService(IPlatformDirectoryService directoryService, int port = 5237, ILogger<TcpFileTransferService>? logger = null)
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
        _directoryService = directoryService;
        _logger = logger ?? NullLogger<TcpFileTransferService>.Instance;
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
            using (var stream = client.GetStream())
            {
                // 设置读取超时
                stream.ReadTimeout = ReadTimeoutMs;

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

    /// <summary>
    /// 处理发送文件请求
    /// </summary>
    private async Task HandleSendFileRequest(NetworkStream stream, TransferRequest request, CancellationToken cancellationToken = default)
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
    private async Task HandleFileData(NetworkStream stream, TransferRequest request, CancellationToken cancellationToken = default)
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

    private async Task OnReceiverCancelled(NetworkStream stream, FileTransferInfo transferInfo, string tempFilePath)
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
    private async Task SendResponseAsync(NetworkStream stream, TransferResponse response, CancellationToken cancellationToken = default)
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
    private async Task SendErrorResponseAsync(string transferId,NetworkStream stream, string message, CancellationToken cancellationToken)
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
        NetworkStream? stream = null;
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

                            var result = await SendFileDataAsync(stream, filePath, transferInfo, cts.Token, timeoutCts).ConfigureAwait(false);
                            //发送流程未被接收方取消
                            if (result)
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
                                    // 接收完成响应
                                    response = await ReceiveResponseAsync(stream, cts.Token).ConfigureAwait(false);

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

    private async Task OnSenderCancelled(NetworkStream stream, FileTransferInfo transferInfo, bool isTimeout = false)
    {
        transferInfo.Status = TransferStatus.Cancelled;
        OnTransferCompleted?.Invoke(transferInfo, isTimeout ? "传输超时" : "传输被发送方取消");
    }

    /// <summary>
    /// 发送文件数据
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="filePath"></param>
    /// <param name="transferInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="timeoutCts">超时取消令牌源，用于在进度更新时重置</param>
    /// <returns>true：完成或自行取消，false：接到接收方取消响应 </returns>
    private async Task<bool> SendFileDataAsync(NetworkStream stream, string filePath, FileTransferInfo transferInfo, CancellationToken cancellationToken = default, CancellationTokenSource? timeoutCts = null)
    {
        transferInfo.Status = TransferStatus.Transferring;
        // 触发传输进度事件，通知UI状态变化
        OnTransferProgressUpdated?.Invoke(transferInfo);

        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            var buffer = new byte[BUFFER_SIZE];
            var totalBytesRead = 0L;

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

            // 发送文件数据
            while (totalBytesRead < transferInfo.FileSize && !cancellationToken.IsCancellationRequested)
            {
                // 检查接收方的取消请求
                try
                {
                    using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    checkCts.CancelAfter(50); // 短超时用于检查
                    var response = await ReceiveResponseAsync(stream, checkCts.Token).ConfigureAwait(false);
                    if (response != null && response.Accepted == false)
                    {
                            //// 接收方取消了传输
                            //transferInfo.Status = TransferStatus.Cancelled;
                            //OnTransferCompleted?.Invoke(transferInfo, response.Message);
                            // 触发取消令牌，停止发送
                        if (_transferCancellationTokens.TryGetValue(transferInfo.TransferId, out var cts))
                        {
                                cts?.Cancel();//这里只取消，处理到上一层处理
                                //cancellationToken.ThrowIfCancellationRequested();
                        }
                        return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常超时，继续发送
                }
                catch (Exception)
                {
                        // 忽略读取请求的异常，继续发送数据
                }

                var bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                await stream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;

                // 更新进度，传递timeoutCts以便在进度更新时重置超时
                UpdateTransferProgress(transferInfo, totalBytesRead, timeoutCts);
            }
            return true;
        }
    }

    /// <summary>
    /// 发送请求
    /// </summary>
    private async Task SendRequestAsync(NetworkStream stream, TransferRequest request, CancellationToken cancellationToken = default)
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
    private async Task<TransferResponse?> ReceiveResponseAsync(NetworkStream stream, CancellationToken cancellationToken = default)
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
    private async Task<byte[]> ReadBytesAsync(NetworkStream stream, int length, CancellationToken cancellationToken = default)
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
