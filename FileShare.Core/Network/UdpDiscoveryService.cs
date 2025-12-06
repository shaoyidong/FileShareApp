using FileShare.Core.Common;
using FileShare.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FileShare.Core.Network;

/// <summary>
/// UDP设备发现服务
/// </summary>
public class UdpDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 5236;
    private const string DiscoveryMessage = "FileShareDiscovery";
    private const int BroadcastIntervalMs = 3000;
    
    private UdpClient? _udpClient; // 改为可空类型，便于状态管理
    private readonly IPEndPoint _broadcastEndPoint;
    private readonly CancellationTokenSource _cts;
    private readonly DeviceInfo _localDevice;
    private readonly List<DeviceInfo> _discoveredDevices;
    private bool _isRunning; // 服务运行状态
    private bool _isDisposed; // 资源是否已释放
    private readonly object _lock = new object(); // 用于线程同步
    
    /// <summary>
    /// 发现设备时触发的事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;
    
    /// <summary>
    /// 发送发现数据包
    /// </summary>
    public void SendDiscoveryPacket()
    {
        if (_isRunning || _isDisposed || _udpClient == null) return;
        
        try
        {
            // 直接发送设备信息
            var deviceInfoJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
            var data = Encoding.UTF8.GetBytes(deviceInfoJson);
            
            _udpClient.SendAsync(data, data.Length, _broadcastEndPoint).Wait();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发送发现数据包失败: {ex.Message}");
        }
    }
    
    public UdpDiscoveryService(string deviceId, string deviceName, DeviceType deviceType)
    {
        _localDevice = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            DeviceType = deviceType,
            Port = 5237, // 文件传输服务端口
            IpAddress = GetLocalIpAddress()
        };
        
        // 初始化UdpClient，但不绑定端口
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        _cts = new CancellationTokenSource();
        _discoveredDevices = new List<DeviceInfo>();
        _isDisposed = false;
    }
    
    /// <summary>
    /// 启动设备发现服务
    /// </summary>
    public Task StartAsync()
    {
        lock (_lock)
        {
            if (_isRunning || _isDisposed) return Task.CompletedTask;
            
            _isRunning = true;
            
            // 在StartAsync中才绑定端口，避免端口占用问题
            if (_udpClient == null)
            {
                _udpClient = new UdpClient();
                _udpClient.EnableBroadcast = true;
            }
            
            try
            {
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"绑定端口失败: {ex.Message}");
                _isRunning = false;
                return Task.CompletedTask;
            }
        }
        
        // 开始监听广播消息
        _ = Task.Run(() => ListenForBroadcastsAsync(_cts.Token));
        
        // 开始发送广播消息
        _ = Task.Run(() => SendBroadcastsAsync(_cts.Token));
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// 停止设备发现服务
    /// </summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (!_isRunning || _isDisposed) return;
            
            _isRunning = false;
            _cts.Cancel();
        }
        
        // 等待异步操作完成
        await Task.Delay(100);
        
        // 安全释放资源
        ReleaseResources();
    }
    
    /// <summary>
    /// 安全释放UdpClient资源
    /// </summary>
    private void ReleaseResources()
    {
        lock (_lock)
        {
            if (_udpClient != null)
            {
                try
                {
                    _udpClient.Close();
                    _udpClient.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"释放UdpClient资源失败: {ex.Message}");
                }
                finally
                {
                    _udpClient = null;
                }
            }
        }
    }
    
    /// <summary>
    /// 监听广播消息
    /// </summary>
    private async Task ListenForBroadcastsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(cancellationToken);
                    var message = System.Text.Encoding.UTF8.GetString(result.Buffer);
                    
                    if (message.StartsWith(DiscoveryMessage))
                    {
                        try
                        {
                            // 解析设备信息
                            var deviceJson = message.Substring(DiscoveryMessage.Length);
                            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(deviceJson, SourceGenerationContext.Default.DeviceInfo);
                            
                            if (deviceInfo != null && deviceInfo.DeviceId != _localDevice.DeviceId)
                            {
                                deviceInfo.IpAddress = result.RemoteEndPoint.Address.ToString();
                                deviceInfo.LastSeen = DateTime.Now;
                                
                                // 更新或添加设备（使用锁保护共享资源）
                                lock (_lock)
                                {
                                    var existingDevice = _discoveredDevices.FirstOrDefault(d => d.DeviceId == deviceInfo.DeviceId);
                                    if (existingDevice == null)
                                    {
                                        _discoveredDevices.Add(deviceInfo);
                                        // 触发设备发现事件
                                        OnDeviceDiscovered?.Invoke(deviceInfo);
                                    }
                                    else
                                    {
                                        existingDevice.LastSeen = DateTime.Now;
                                        existingDevice.IpAddress = deviceInfo.IpAddress;
                                    }
                                   
                                }
                                
                                // 发送回应消息
                                if (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
                                {
                                    await SendResponseAsync(result.RemoteEndPoint);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"解析设备信息失败: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                    {
                        Console.WriteLine($"接收广播消息失败: {ex.Message}");
                    }
                    // 短暂延迟后继续尝试
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Console.WriteLine($"监听广播线程异常: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 发送广播消息
    /// </summary>
    private async Task SendBroadcastsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
            {
                try
                {
                    var deviceJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
                    var message = DiscoveryMessage + deviceJson;
                    var data = System.Text.Encoding.UTF8.GetBytes(message);
                    
                    await _udpClient.SendAsync(data, data.Length, _broadcastEndPoint);
                    
                    // 清理过期设备（使用锁保护共享资源）
                    lock (_lock)
                    {
                        CleanupExpiredDevices();
                    }
                    
                    await Task.Delay(BroadcastIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                    {
                        Console.WriteLine($"发送广播消息失败: {ex.Message}");
                    }
                    // 短暂延迟后继续尝试
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发送广播线程异常: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 发送回应消息
    /// </summary>
    private async Task SendResponseAsync(IPEndPoint remoteEndPoint)
    {
        if (_isDisposed || _udpClient == null) return;
        
        try
        {
            var deviceJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
            var message = DiscoveryMessage + deviceJson;
            var data = System.Text.Encoding.UTF8.GetBytes(message);
            
            await _udpClient.SendAsync(data, data.Length, remoteEndPoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发送回应消息失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 清理过期设备
    /// </summary>
    private void CleanupExpiredDevices()
    {
        var cutoffTime = DateTime.Now.AddSeconds(-10);
        _discoveredDevices.RemoveAll(d => d.LastSeen < cutoffTime);
    }
    
    /// <summary>
    /// 获取本地IP地址
    /// </summary>
    private string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取本地IP地址失败: {ex.Message}");
        }
        return "127.0.0.1";
    }
    
    /// <summary>
    /// 获取当前发现的设备列表
    /// </summary>
    public List<DeviceInfo> GetDiscoveredDevices()
    {
        lock (_lock)
        {
            return new List<DeviceInfo>(_discoveredDevices);
        }
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    /// <summary>
    /// 释放资源（重载）
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                // 取消正在运行的任务
                _cts.Cancel();
                
                // 释放托管资源
                ReleaseResources();
                
                // 释放CancellationTokenSource
                _cts.Dispose();
            }
            
            _isDisposed = true;
        }
    }
    
    /// <summary>
    /// 析构函数
    /// </summary>
    ~UdpDiscoveryService()
    {
        Dispose(false);
    }
}