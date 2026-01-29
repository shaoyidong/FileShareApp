using FileShare.Core.Common;
using FileShare.Core.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;

namespace FileShare.Core.Network;

/// <summary>
/// UDP设备发现服务
/// </summary>
public class UdpDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 5236;
    private const string DiscoveryMessage = "FileShareDiscovery";
    
    // 广播间隔配置
    private const int MaxBroadcastIntervalMs = 15000; // 最大广播间隔
    private const int InitialBroadcastIntervalMs = 1000; // 初始广播间隔
    private const int StableBroadcastIntervalMs = 5000; // 稳定状态广播间隔
    private const int FastDiscoveryCount = 5; // 快速发现阶段的广播次数
    
    // 设备过期时间
    private const int DeviceExpirySeconds = 20; // 设备过期时间
    
    // 回应消息节流
    private const int MinResponseIntervalMs = 500; // 最小回应间隔
    private readonly Dictionary<string, DateTime> _lastResponseTimes = new(); // 记录对每个设备的最后回应时间
    
    // 网络拥塞检测
    private int _failedBroadcasts = 0; // 失败的广播次数
    private const int MaxFailedBroadcasts = 3; // 最大失败次数阈值
    
    private UdpClient? _udpClient; // 改为可空类型，便于状态管理
    private readonly IPEndPoint _broadcastEndPoint;
    private readonly CancellationTokenSource _cts;
    private readonly DeviceInfo _localDevice;
    private readonly List<DeviceInfo> _discoveredDevices;
    private bool _isRunning; // 服务运行状态
    private bool _isDisposed; // 资源是否已释放
    private readonly object _lock = new object(); // 用于线程同步
    private int _broadcastCount = 0; // 广播计数器
    private int _currentBroadcastIntervalMs = InitialBroadcastIntervalMs; // 当前广播间隔
    
    /// <summary>
    /// 发现设备时触发的事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;
    
    /// <summary>
    /// 设备离线时触发的事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceRemoved;
    
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

    public UdpDiscoveryService(DeviceInfo localDevice)
    {
        _localDevice = localDevice;
        
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
            _broadcastCount = 0;
            _currentBroadcastIntervalMs = InitialBroadcastIntervalMs;
            _failedBroadcasts = 0;
            
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
        await Task.Delay(100).ConfigureAwait(false);
        
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
                    var result = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
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
                                
                                // 智能发送回应消息（节流）
                                if (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
                                {
                                    await SendResponseWithThrottlingAsync(result.RemoteEndPoint).ConfigureAwait(false);
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
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
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
                    
                    await _udpClient.SendAsync(data, data.Length, _broadcastEndPoint).ConfigureAwait(false);
                    
                    // 重置失败计数
                    _failedBroadcasts = 0;
                    
                    // 清理过期设备（使用锁保护共享资源）
                    lock (_lock)
                    {
                        CleanupExpiredDevices();
                    }
                    
                    // 调整广播间隔
                    AdjustBroadcastInterval();
                    
                    await Task.Delay(_currentBroadcastIntervalMs, cancellationToken).ConfigureAwait(false);
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
                        Debug.WriteLine($"发送广播消息失败: {ex.Message}");
                        _failedBroadcasts++;
                        
                        // 网络拥塞检测
                        if (_failedBroadcasts >= MaxFailedBroadcasts)
                        {
                            // 增加广播间隔以减轻网络负担
                            lock (_lock)
                            {
                                _currentBroadcastIntervalMs = Math.Min(_currentBroadcastIntervalMs * 2, MaxBroadcastIntervalMs);
                            }
                            Debug.WriteLine($"网络可能拥塞，增加广播间隔到 {_currentBroadcastIntervalMs}ms");
                            _failedBroadcasts = 0;
                        }
                    }
                    // 短暂延迟后继续尝试
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
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
    /// 调整广播间隔
    /// </summary>
    private void AdjustBroadcastInterval()
    {
        lock (_lock)
        {
            _broadcastCount++;
            
            // 快速发现阶段
            if (_broadcastCount <= FastDiscoveryCount)
            {
                _currentBroadcastIntervalMs = InitialBroadcastIntervalMs;
            }
            // 稳定阶段
            else
            {
                int deviceCount;
                lock (_lock)
                {
                    deviceCount = _discoveredDevices.Count;
                }
                
                // 根据设备数量调整广播间隔
                if (deviceCount == 0)
                {
                    // 没有发现设备，保持较短间隔
                    _currentBroadcastIntervalMs = Math.Min(StableBroadcastIntervalMs, _currentBroadcastIntervalMs);
                }
                else if (deviceCount <= 1)
                {
                    // 发现少量设备，使用中等间隔
                    _currentBroadcastIntervalMs = StableBroadcastIntervalMs;
                }
                else
                {
                    // 发现多个设备，使用较长间隔
                    _currentBroadcastIntervalMs = Math.Min(StableBroadcastIntervalMs * 2, MaxBroadcastIntervalMs);
                }
            }
        }
    }
    
    /// <summary>
    /// 带节流的发送回应消息
    /// </summary>
    private async Task SendResponseWithThrottlingAsync(IPEndPoint remoteEndPoint)
    {
        var deviceKey = remoteEndPoint.Address.ToString();
        
        lock (_lock)
        {
            // 检查是否需要节流
            if (_lastResponseTimes.TryGetValue(deviceKey, out var lastResponseTime))
            {
                var timeSinceLastResponse = DateTime.Now - lastResponseTime;
                if (timeSinceLastResponse.TotalMilliseconds < MinResponseIntervalMs)
                {
                    // 未到最小回应间隔，跳过
                    return;
                }
            }
            
            // 更新最后回应时间
            _lastResponseTimes[deviceKey] = DateTime.Now;
        }
        
        await SendResponseAsync(remoteEndPoint).ConfigureAwait(false);
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
            
            await _udpClient.SendAsync(data, data.Length, remoteEndPoint).ConfigureAwait(false);
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
        var cutoffTime = DateTime.Now.AddSeconds(-DeviceExpirySeconds);
        
        // 找出所有过期的设备
        var expiredDevices = _discoveredDevices.Where(d => d.LastSeen < cutoffTime).ToList();
        
        // 移除过期设备
        _discoveredDevices.RemoveAll(d => d.LastSeen < cutoffTime);
        
        // 触发设备离线事件
        foreach (var device in expiredDevices)
        {
            OnDeviceRemoved?.Invoke(device);
        }
        
        // 清理过期的回应时间记录
        var expiredKeys = _lastResponseTimes.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > DeviceExpirySeconds).Select(kv => kv.Key).ToList();
        foreach (var key in expiredKeys)
        {
            _lastResponseTimes.Remove(key);
        }
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