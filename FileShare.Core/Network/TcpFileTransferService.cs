using FileShare.Core.Common;
using FileShare.Core.Models;
using FileShare.Core.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<string, FileTransferInfo> _incomingTransfers; // 接收的文件传输
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingTransferRequests; // 等待用户确认的传输请求
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferCancellationTokens; // 传输取消令牌
    private readonly ConcurrentDictionary<string, double> _lastProgressValues; // 上次进度值，用于优化事件触发
    private readonly IPlatformDirectoryService _directoryService;
    private bool _disposedValue;

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

    public TcpFileTransferService(IPlatformDirectoryService directoryService,int port = 5237)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _cts = new CancellationTokenSource();
        _incomingTransfers = new ConcurrentDictionary<string, FileTransferInfo>();
        _pendingTransferRequests = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        _transferCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
        _lastProgressValues = new ConcurrentDictionary<string, double>(); // 初始化进度跟踪字典
        _directoryService = directoryService;
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
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止文件传输服务
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        _listener.Stop();
        // 取消所有等待中的请求
        foreach (var tcs in _pendingTransferRequests.Values)
        {
            tcs?.TrySetCanceled();
        }
        _pendingTransferRequests.Clear();

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
    }

    /// <summary>
    /// 异步停止文件传输服务
    /// </summary>
    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    #region 接收文件
    /// <summary>
    /// 接受连接请求
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception)
        {
            Console.WriteLine("接受连接失败");
        }
    }

    /// <summary>
    /// 处理客户端连接
    /// </summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string? senderId = null;
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // 循环处理多个请求，直到连接关闭
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        // 检查是否有可用数据
                        if (!stream.DataAvailable)
                        {
                            // 如果没有数据且连接仍然打开，等待一小段时间
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        // 读取请求头
                        var headerBytes = await ReadBytesAsync(stream, 4, cancellationToken).ConfigureAwait(false);
                        if (headerBytes.Length == 0) break; // 连接关闭

                        var headerLength = BitConverter.ToInt32(headerBytes, 0);
                        var requestBytes = await ReadBytesAsync(stream, headerLength, cancellationToken).ConfigureAwait(false);
                        if (requestBytes.Length == 0) break; // 连接关闭

                        var requestJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                        var request = JsonSerializer.Deserialize<TransferRequest>(requestJson, SourceGenerationContext.Default.TransferRequest);

                        if (request != null)
                        {
                            // 保存发送方ID，用于后续清理
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
                    catch (IOException ex) when (ex.Message.Contains("你的主机中的软件中止了一个已建立的连接"))
                    {
                        Console.WriteLine("客户端连接被中止: {0}", ex.Message);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("处理请求被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("处理请求失败: {0} - {1}", ex.GetType().Name, ex.Message);
                        // 发送错误响应
                        var response = new TransferResponse
                        {
                            TransferId = "unknown",
                            Accepted = false,
                            Message = "处理请求失败: " + ex.Message
                        };

                        try
                        {
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
            Console.WriteLine("处理客户端连接失败: {0} - {1}", ex.GetType().Name, ex.Message);
        }
        finally
        {
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
                // 超时
                accepted = false;
                transferInfo.Status = TransferStatus.Cancelled;
                OnTransferCompleted?.Invoke(transferInfo, "超时未选择");
            }
            else
            {
                // 用户做出了选择
                accepted = await tcs.Task.ConfigureAwait(false);
                if (!accepted)
                {
                    transferInfo.Status = TransferStatus.Cancelled;
                    OnTransferCompleted?.Invoke(transferInfo, "拒绝");
                }
                // 接受请求时保持Pending状态，实际传输开始时会在HandleFileData中设置为Transferring
            }
            // 如果拒绝了请求，移除传输信息
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
        if (_incomingTransfers.TryGetValue(request.TransferId, out var transferInfo))
        {
            var savePath = transferInfo.SavePath;
            if (string.IsNullOrEmpty(savePath))
            {
                savePath = FileTypeHelper.GetDirectoryByFileType(request.FileName, _directoryService);
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

                // 确保目标目录存在
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
                    // 传输完成
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
                        // 发送错误响应
                        var response = new TransferResponse
                        {
                            TransferId = transferInfo.TransferId,
                            Accepted = false,
                            Message = "文件接收失败: 传输中断"
                        };

                        await SendResponseAsync(stream, response, cts.Token).ConfigureAwait(false);                       
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

                // 删除临时文件
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }

                try
                {
                    // 发送错误响应
                    var response = new TransferResponse
                    {
                        TransferId = transferInfo.TransferId,
                        Accepted = false,
                        Message = "文件接收失败: " + ex.Message
                    };

                    await SendResponseAsync(stream, response, cts.Token).ConfigureAwait(false);                 
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
                _lastProgressValues.TryRemove(transferInfo.TransferId, out _); // 清理进度跟踪数据
                cts?.Dispose();
            }
        }
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
            var cancelResponse = new TransferResponse
            {
                TransferId = transferInfo.TransferId,
                Accepted = false,
                Message = "传输被接收方取消",
            };
            await SendResponseAsync(stream, cancelResponse, _cts.Token).ConfigureAwait(false);            
        }
        catch (Exception)
        {
            // 忽略发送取消响应的异常
        }
    }

    /// <summary>
    /// 发送响应
    /// <summary>
    private async Task SendResponseAsync(NetworkStream stream, TransferResponse response, CancellationToken cancellationToken = default)
    {
        var responseJson = JsonSerializer.Serialize(response, SourceGenerationContext.Default.TransferResponse);
        var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
        var headerBytes = BitConverter.GetBytes(responseBytes.Length);

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    #endregion

    #region 发送文件
    /// <summary>
    /// 发送文件到指定设备
    /// <summary>
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
                Console.WriteLine("文件不存在: {0}", filePath);
                return false;
            }

            var fileInfo = new FileInfo(filePath);
            transferId = Guid.NewGuid().ToString();

            // 创建传输信息
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
            // 创建取消令牌源并保存到字典中
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
                            ReceiverId = targetDevice.DeviceId
                        };

                        await SendRequestAsync(stream, request, cts.Token).ConfigureAwait(false);

                        // 接收响应
                        var response = await ReceiveResponseAsync(stream, cts.Token).ConfigureAwait(false);

                        if (response?.Accepted??false)
                        {
                            // 发送文件数据，增加文件传输的超时时间
                            // 初始设置超时，后续会根据进度更新重置
                            timeoutCts.CancelAfter(TimeSpan.FromMicroseconds(REQUEST_TIMEOUT_MS));

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

                                    if (response?.Accepted??false)
                                    {
                                        transferInfo.Status = TransferStatus.Completed;
                                        OnTransferCompleted?.Invoke(transferInfo, null);
                                        return true;
                                    }
                                    else
                                    {
                                        Console.WriteLine("文件传输被接收方拒绝: {0}", response?.Message);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("文件请求被接收方拒绝: {0}", response?.Message);
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
        catch (IOException ex) when (ex.Message.Contains("你的主机中的软件中止了一个已建立的连接"))
        {
            Console.WriteLine("连接被接收方中断: {0}", ex.Message);
            // 更新传输状态
            if (transferInfo != null)
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo, "连接被接收方中断");
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("文件传输失败: {0} - {1}", ex.GetType().Name, ex.Message);
            // 更新传输状态
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
                _lastProgressValues.TryRemove(transferId, out _); // 清理进度跟踪数据
                userCts?.Dispose();
                timeoutCts?.Dispose();
                // 清理传输连接信息
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
                // 检查是否有来自接收方的取消请求
                if (stream.DataAvailable)
                {
                    try
                    {
                        var response = await ReceiveResponseAsync(stream, cancellationToken).ConfigureAwait(false);
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
                    catch (Exception)
                    {
                        // 忽略读取请求的异常，继续发送数据
                    }
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
    /// <summary>
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
    /// <summary>
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
    /// <summary>
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
            // 更新上次进度值
            _lastProgressValues[transferInfo.TransferId] = currentProgress;

            // 重置超时时间，只要有进度更新就不触发超时
            timeoutCts?.CancelAfter(TimeSpan.FromMicroseconds(REQUEST_TIMEOUT_MS));
        }
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
/// <summary>
public enum TransferRequestType
{
    SendFileRequest,
    SendFileData,
}

/// <summary>
/// 传输请求
/// <summary>
public class TransferRequest
{
    public TransferRequestType Type { get; set; }
    public required string TransferId { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public required string SenderId { get; set; }
    public required string ReceiverId { get; set; }
}

/// <summary>
/// 传输响应
/// <summary>
public class TransferResponse
{
    public required string TransferId { get; set; }
    public bool Accepted { get; set; }
    public string? Message { get; set; }
}