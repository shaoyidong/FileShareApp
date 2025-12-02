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
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, FileTransferInfo> _activeTransfers;
    
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
                // 读取请求头
                var headerBytes = await ReadBytesAsync(stream, 4);
                var headerLength = BitConverter.ToInt32(headerBytes, 0);
                var requestBytes = await ReadBytesAsync(stream, headerLength);
                var requestJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                
                var request = JsonSerializer.Deserialize<TransferRequest>(requestJson, SourceGenerationContext.Default.TransferRequest);
                
                if (request != null)
                {
                    switch (request.Type)
                    {
                        case TransferRequestType.SendFileRequest:
                            await HandleSendFileRequest(stream, request);
                            break;
                        case TransferRequestType.SendFileData:
                            await HandleFileData(stream, request);
                            break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 异常已记录
        }
    }
    
    /// <summary>
    /// 处理发送文件请求
    /// </summary>
    private async Task HandleSendFileRequest(NetworkStream stream, TransferRequest request)
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
        
        // 触发传输请求事件
        OnTransferRequestReceived?.Invoke(transferInfo);
        
        // 保存传输信息
        _activeTransfers[transferInfo.TransferId] = transferInfo;
        
        // 发送接受响应
        var response = new TransferResponse
        {
            TransferId = transferInfo.TransferId,
            Accepted = true,
            Message = "准备接收文件"
        };
        
        await SendResponseAsync(stream, response);
    }
    
    /// <summary>
    /// 处理文件数据
    /// </summary>
    private async Task HandleFileData(NetworkStream stream, TransferRequest request)
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
                    
                    while (totalBytesRead < request.FileSize)
                    {
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;
                        
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
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
                
                await SendResponseAsync(stream, response);
            }
            catch (Exception)
            {
                transferInfo.Status = TransferStatus.Failed;
                OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
                
                // 删除临时文件
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
        }
    }
    
    /// <summary>
    /// 发送文件到指定设备
    /// <summary>
    public async Task<bool> SendFileAsync(string filePath, DeviceInfo targetDevice, string senderId)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine("文件不存在");
                return false;
            }
            
            var fileInfo = new FileInfo(filePath);
            var transferId = Guid.NewGuid().ToString();
            
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
            
            // 连接到目标设备
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(targetDevice.IpAddress, targetDevice.Port);
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
                    
                    await SendRequestAsync(stream, request);
                    
                    // 接收响应
                    var response = await ReceiveResponseAsync(stream);
                    
                    if (response.Accepted)
                    {
                        // 发送文件数据
                        transferInfo.Status = TransferStatus.Transferring;
                        await SendFileDataAsync(stream, filePath, transferInfo);
                        
                        // 接收完成响应
                        response = await ReceiveResponseAsync(stream);
                        
                        if (response.Accepted)
                        {
                            transferInfo.Status = TransferStatus.Completed;
                            OnTransferCompleted?.Invoke(transferInfo.TransferId, false, "用户拒绝");
                            return true;
                        }
                    }
                }
            }
            
            transferInfo.Status = TransferStatus.Failed;
            OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, transferInfo.TransferredSize, transferInfo.FileSize);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    /// <summary>
    /// 发送文件数据
    /// <summary>
    private async Task SendFileDataAsync(NetworkStream stream, string filePath, FileTransferInfo transferInfo)
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
            
            await SendRequestAsync(stream, request);
            
            // 发送文件数据
            while (totalBytesRead < transferInfo.FileSize)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                
                await stream.WriteAsync(buffer, 0, bytesRead);
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
    private async Task SendRequestAsync(NetworkStream stream, TransferRequest request)
    {
        var requestJson = JsonSerializer.Serialize(request, SourceGenerationContext.Default.TransferRequest);
        var requestBytes = System.Text.Encoding.UTF8.GetBytes(requestJson);
        var headerBytes = BitConverter.GetBytes(requestBytes.Length);
        
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(requestBytes);
    }
    
    /// <summary>
    /// 接收响应
    /// <summary>
    private async Task<TransferResponse> ReceiveResponseAsync(NetworkStream stream)
    {
        var headerBytes = await ReadBytesAsync(stream, 4);
        var headerLength = BitConverter.ToInt32(headerBytes, 0);
        var responseBytes = await ReadBytesAsync(stream, headerLength);
        var responseJson = System.Text.Encoding.UTF8.GetString(responseBytes);
        
        return JsonSerializer.Deserialize<TransferResponse>(responseJson, SourceGenerationContext.Default.TransferResponse);
    }
    
    /// <summary>
    /// 发送响应
    /// <summary>
    private async Task SendResponseAsync(NetworkStream stream, TransferResponse response)
    {
        var responseJson = JsonSerializer.Serialize(response, SourceGenerationContext.Default.TransferResponse);
        var responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
        var headerBytes = BitConverter.GetBytes(responseBytes.Length);
        
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(responseBytes);
    }
    
    /// <summary>
    /// 读取指定长度的字节
    /// <summary>
    private async Task<byte[]> ReadBytesAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        var totalBytesRead = 0;
        
        while (totalBytesRead < length)
        {
            var bytesRead = await stream.ReadAsync(buffer, totalBytesRead, length - totalBytesRead);
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