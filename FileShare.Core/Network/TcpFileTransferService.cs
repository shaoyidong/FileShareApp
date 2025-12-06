using FileShare.Core.Common;
using FileShare.Core.Models;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FileShare.Core.Network;

/// <summary>
/// TCP文件传输服务
/// </summary>
public class TcpFileTransferService : IDisposable
{
    private const int BufferSize = 65536; // 64KB缓冲区
    private const int RequestTimeoutMs = 30000; // 请求超时时间（30秒）
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, FileTransferInfo> _activeTransfers;
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingTransferRequests; // 等待用户确认的传输请求
    
    /// <summary>
    /// 接收到文件传输请求时触发
    /// </summary>
    public event Action<FileTransferInfo>? OnTransferRequestReceived;
    
    /// <summary>
    /// 传输进度更新事件
    /// </summary>
    public event Action<string, long, long>? OnTransferProgressUpdated;
    
    /// <summary>
    /// 传输完成事件
    /// </summary>
    public event Action<string, bool, string?>? OnTransferCompleted;
    
    public TcpFileTransferService(int port = 5237)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _cts = new CancellationTokenSource();
        _activeTransfers = new Dictionary<string, FileTransferInfo>();
        _pendingTransferRequests = new Dictionary<string, TaskCompletionSource<bool>>();
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
    }
    
    /// <summary>
    /// 处理用户对传输请求的选择
    /// </summary>
    /// <param name="transferId">传输ID</param>
    /// <param name="accept">是否接受请求</param>
    public void HandleTransferRequest(string transferId, bool accept)
    {
        lock (_pendingTransferRequests)
        {
            if (_pendingTransferRequests.TryGetValue(transferId, out var tcs))
            {
                tcs.TrySetResult(accept);
                _pendingTransferRequests.Remove(transferId);
            }
        }
    }
    
    /// <summary>
    /// 异步停止文件传输服务
    /// </summary>
    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// 接受连接请求
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
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
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }
                        
                        // 读取请求头
                        var headerBytes = await ReadBytesAsync(stream, 4, cancellationToken);
                        if (headerBytes.Length == 0) break; // 连接关闭
                        
                        var headerLength = BitConverter.ToInt32(headerBytes, 0);
                        var requestBytes = await ReadBytesAsync(stream, headerLength, cancellationToken);
                        if (requestBytes.Length == 0) break; // 连接关闭
                        
                        var requestJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                        var request = JsonSerializer.Deserialize<TransferRequest>(requestJson, SourceGenerationContext.Default.TransferRequest);
                        
                        if (request != null)
                        {
                            switch (request.Type)
                            {
                                case TransferRequestType.SendFileRequest:
                                    await HandleSendFileRequest(stream, request, cancellationToken);
                                    break;
                                case TransferRequestType.SendFileData:
                                    await HandleFileData(stream, request, cancellationToken);
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
                            await SendResponseAsync(stream, response, cancellationToken);
                            await stream.FlushAsync(cancellationToken);
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
            Status = TransferStatus.Pending
        };
        
        // 创建TaskCompletionSource来等待用户选择
        var tcs = new TaskCompletionSource<bool>();
        
        // 保存传输信息和等待任务
        _activeTransfers[transferInfo.TransferId] = transferInfo;
        lock (_pendingTransferRequests)
        {
            _pendingTransferRequests[transferInfo.TransferId] = tcs;
        }
        
        try
        {
            // 触发传输请求事件
            OnTransferRequestReceived?.Invoke(transferInfo);
            
            // 等待用户选择，设置超时
            var timeoutTask = Task.Delay(RequestTimeoutMs, cancellationToken);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            bool accepted;
            if (completedTask == timeoutTask)
            {
                // 超时
                accepted = false;
                transferInfo.Status = TransferStatus.Cancelled;
                OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, 0, 0);
            }
            else
            {
                // 用户做出了选择
                accepted = await tcs.Task;
                if (!accepted)
                {
                    transferInfo.Status = TransferStatus.Cancelled;
                    OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, 0, 0);
                }
                // 接受请求时保持Pending状态，实际传输开始时会在HandleFileData中设置为Transferring
            }
            
            // 发送响应
            var response = new TransferResponse
            {
                TransferId = transferInfo.TransferId,
                Accepted = accepted,
                Message = accepted ? "准备接收文件" : "文件传输请求已被拒绝"
            };
            
            await SendResponseAsync(stream, response, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            
            // 如果拒绝了请求，清理传输信息
            if (!accepted)
            {
                _activeTransfers.Remove(transferInfo.TransferId);
            }
        }
        finally
        {
            // 清理等待任务
            lock (_pendingTransferRequests)
            {
                _pendingTransferRequests.Remove(transferInfo.TransferId);
            }
        }
    }
    
    /// <summary>
    /// 处理文件数据
    /// </summary>
    private async Task HandleFileData(NetworkStream stream, TransferRequest request, CancellationToken cancellationToken = default)
    {
        if (_activeTransfers.TryGetValue(request.TransferId, out var transferInfo))
        {
            transferInfo.Status = TransferStatus.Transferring;
            
            // 接收文件数据
            var tempFilePath = Path.GetTempFileName();
            try
            {
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                {
                    var totalBytesRead = 0L;
                    var buffer = new byte[BufferSize];
                    
                    while (totalBytesRead < request.FileSize && !cancellationToken.IsCancellationRequested)
                    {
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        if (bytesRead == 0) break;
                        
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        totalBytesRead += bytesRead;
                        
                        // 更新进度
                        transferInfo.TransferredSize = totalBytesRead;
                        OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
                    }
                }
                
                // 传输完成
                transferInfo.Status = TransferStatus.Completed;
                transferInfo.FileName = request.FileName;
                
                OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
                OnTransferCompleted?.Invoke(transferInfo.TransferId, true, null);
                
                // 发送完成响应
                var response = new TransferResponse
                {
                    TransferId = transferInfo.TransferId,
                    Accepted = true,
                    Message = "文件接收完成"
                };
                
                await SendResponseAsync(stream, response, cancellationToken);
                // 确保响应已发送完成
                await stream.FlushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
                
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
                    
                    await SendResponseAsync(stream, response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                catch (Exception)
                {
                    // 忽略发送错误响应时的异常
                }
            }
        }
    }
    
    /// <summary>
    /// 发送文件到指定设备
    /// <summary>
    public async Task<bool> SendFileAsync(string filePath, DeviceInfo targetDevice, string senderId)
    {
        string transferId = null;
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
            var transferInfo = new FileTransferInfo
            {
                TransferId = transferId,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                SenderId = senderId,
                ReceiverId = targetDevice.DeviceId,
                Status = TransferStatus.Pending
            };
            
            _activeTransfers[transferId] = transferInfo;
            
            // 设置默认超时时间为30秒
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var client = new TcpClient())
            {
                // 连接到目标设备，添加超时
                await client.ConnectAsync(targetDevice.IpAddress, targetDevice.Port, cts.Token);
                using (var stream = client.GetStream())
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
                    
                    await SendRequestAsync(stream, request, cts.Token);
                    
                    // 接收响应
                    var response = await ReceiveResponseAsync(stream, cts.Token);
                    
                    if (response.Accepted)
                    {
                        // 发送文件数据，增加文件传输的超时时间
                        cts.CancelAfter(TimeSpan.FromMinutes(5)); // 大文件传输超时设置为5分钟
                        transferInfo.Status = TransferStatus.Transferring;
                        await SendFileDataAsync(stream, filePath, transferInfo, cts.Token);
                        
                        // 接收完成响应
                        response = await ReceiveResponseAsync(stream, cts.Token);
                        
                        if (response.Accepted)
                        {
                            transferInfo.Status = TransferStatus.Completed;
                            OnTransferCompleted?.Invoke(transferInfo.TransferId, false, null);
                            return true;
                        }
                        else
                        {
                            Console.WriteLine("文件传输被接收方拒绝: {0}", response.Message);
                        }
                    }
                    else
                    {
                        Console.WriteLine("文件请求被接收方拒绝: {0}", response.Message);
                    }
                }
            }
            
            transferInfo.Status = TransferStatus.Failed;
            OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
            OnTransferCompleted?.Invoke(transferInfo.TransferId, false, "传输被接收方拒绝");
            return false;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("文件传输超时");
            // 更新传输状态
            if (transferId != null && _activeTransfers.TryGetValue(transferId, out var transferInfo))
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo.TransferId, false, "传输超时");
            }
            return false;
        }
        catch (IOException ex) when (ex.Message.Contains("你的主机中的软件中止了一个已建立的连接"))
        {
            Console.WriteLine("连接被接收方中断: {0}", ex.Message);
            // 更新传输状态
            if (transferId != null && _activeTransfers.TryGetValue(transferId, out var transferInfo))
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo.TransferId, false, "连接被接收方中断");
            }
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("文件传输失败: {0} - {1}", ex.GetType().Name, ex.Message);
            // 更新传输状态
            if (transferId != null && _activeTransfers.TryGetValue(transferId, out var transferInfo))
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferCompleted?.Invoke(transferInfo.TransferId, false, ex.Message);
            }
            return false;
        }
    }
    
    /// <summary>
    /// 发送文件数据
    /// <summary>
    private async Task SendFileDataAsync(NetworkStream stream, string filePath, FileTransferInfo transferInfo, CancellationToken cancellationToken = default)
    {
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        {
            var buffer = new byte[BufferSize];
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
            
            await SendRequestAsync(stream, request, cancellationToken);
            
            // 发送文件数据
            while (totalBytesRead < transferInfo.FileSize && !cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) break;
                
                await stream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalBytesRead += bytesRead;
                
                // 更新进度
                transferInfo.TransferredSize = totalBytesRead;
                OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
            }
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
        
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(requestBytes, cancellationToken);
    }
    
    /// <summary>
    /// 接收响应
    /// <summary>
    private async Task<TransferResponse> ReceiveResponseAsync(NetworkStream stream, CancellationToken cancellationToken = default)
    {
        var headerBytes = await ReadBytesAsync(stream, 4, cancellationToken);
        var headerLength = BitConverter.ToInt32(headerBytes, 0);
        var responseBytes = await ReadBytesAsync(stream, headerLength, cancellationToken);
        var responseJson = System.Text.Encoding.UTF8.GetString(responseBytes);
        
        return JsonSerializer.Deserialize<TransferResponse>(responseJson, SourceGenerationContext.Default.TransferResponse);
    }
    
    /// <summary>
    /// 发送响应
    /// <summary>
    private async Task SendResponseAsync(NetworkStream stream, TransferResponse response, CancellationToken cancellationToken = default)
    {
        var responseJson = JsonSerializer.Serialize(response, SourceGenerationContext.Default.TransferResponse);
        var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
        var headerBytes = BitConverter.GetBytes(responseBytes.Length);
        
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);
    }
    
    /// <summary>
    /// 读取指定长度的字节
    /// <summary>
    private async Task<byte[]> ReadBytesAsync(NetworkStream stream, int length, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[length];
        var totalBytesRead = 0;
        
        while (totalBytesRead < length)
        {
            var bytesRead = await stream.ReadAsync(buffer, totalBytesRead, length - totalBytesRead, cancellationToken);
            if (bytesRead == 0)
                throw new EndOfStreamException();
            
            totalBytesRead += bytesRead;
        }
        
        return buffer;
    }
    
    public void Dispose()
    {
        Stop();
        _listener?.Stop();
        _cts?.Dispose();
        
        // 取消所有等待中的请求
        lock (_pendingTransferRequests)
        {
            foreach (var tcs in _pendingTransferRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingTransferRequests.Clear();
        }
    }
}

/// <summary>
/// 传输请求类型
/// <summary>
public enum TransferRequestType
{
    SendFileRequest,
    SendFileData
}

/// <summary>
/// 传输请求
/// <summary>
public class TransferRequest
{
    public TransferRequestType Type { get; set; }
    public string TransferId { get; set; }
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public string SenderId { get; set; }
    public string ReceiverId { get; set; }
}

/// <summary>
/// 传输响应
/// <summary>
public class TransferResponse
{
    public string TransferId { get; set; }
    public bool Accepted { get; set; }
    public string Message { get; set; }
}