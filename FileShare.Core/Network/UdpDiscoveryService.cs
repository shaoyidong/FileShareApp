using FileShare.Core.Common;
using FileShare.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
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

    // 广播间隔配置
    private const int MaxBroadcastIntervalMs = 15000; // 最大广播间隔
    private const int InitialBroadcastIntervalMs = 1000; // 初始广播间隔
    private const int StableBroadcastIntervalMs = 5000; // 稳定状态广播间隔
    private const int FastDiscoveryCount = 5; // 快速发现阶段的广播次数

    // 设备过期时间
    private const int DeviceExpirySeconds = 20; // 设备过期时间

    // 回应消息节流
    private const int MinResponseIntervalMs = 500; // 最小回应间隔

    // 最大允许设备数量（防止恶意广播耗尽内存）
    private const int MaxDiscoveredDevices = 200;

    // 并发集合
    private readonly ConcurrentDictionary<string, DateTime> _lastResponseTimes = new();
    private readonly ConcurrentDictionary<string, DeviceInfo> _discoveredDevices = new();

    // 网络拥塞检测
    private int _failedBroadcasts = 0; // 失败的广播次数
    private const int MaxFailedBroadcasts = 3; // 最大失败次数阈值

    private UdpClient? _udpClient;
    private readonly IPEndPoint _broadcastEndPoint;
    private readonly CancellationTokenSource _cts;
    private readonly DeviceInfo _localDevice;
    private int _broadcastCount = 0;// 广播计数器
    private volatile int _currentBroadcastIntervalMs = InitialBroadcastIntervalMs;// 当前广播间隔
    private volatile bool _isRunning;// 服务运行状态
    private volatile bool _isDisposed;// 资源是否已释放

    /// <summary>
    /// 发现设备时触发的事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;

    /// <summary>
    /// 设备离线时触发的事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceRemoved;

    public UdpDiscoveryService(DeviceInfo localDevice)
    {
        _localDevice = localDevice;
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        _cts = new CancellationTokenSource();
        _isDisposed =false;
    }

    /// <summary>
    /// 异步发送发现数据包
    /// </summary>
    public async Task SendDiscoveryPacketAsync()
    {
        if (_isRunning || _isDisposed || _udpClient == null) return;

        try
        {
            // 直接发送设备信息
            var deviceInfoJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
            var data = Encoding.UTF8.GetBytes(deviceInfoJson);

            await _udpClient.SendAsync(data, data.Length, _broadcastEndPoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"发送发现数据包失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 启动设备发现服务
    /// </summary>
    public Task StartAsync()
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
            Debug.WriteLine($"绑定端口失败: {ex.Message}");
                _isRunning = false;
            return Task.CompletedTask;
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
        if (!_isRunning || _isDisposed) return;
        _isRunning = false;
        _cts.Cancel();

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
        if (_udpClient != null)
        {
            try
            {
                _udpClient.Close();
                _udpClient.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"释放UdpClient资源失败: {ex.Message}");
            }
            finally
            {
                _udpClient = null;
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
                    var message = Encoding.UTF8.GetString(result.Buffer);

                    if (message.StartsWith(DiscoveryMessage, StringComparison.Ordinal))
                    {
                        try
                        {
                            // 解析设备信息
                            var deviceJson = message.Substring(DiscoveryMessage.Length);
                            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(deviceJson, SourceGenerationContext.Default.DeviceInfo);

                            if (deviceInfo != null
                                && !string.IsNullOrEmpty(deviceInfo.DeviceId)
                                && deviceInfo.DeviceId != _localDevice.DeviceId
                                && !string.IsNullOrEmpty(deviceInfo.DeviceName))
                            {
                                deviceInfo.IpAddress = result.RemoteEndPoint.Address.ToString();
                                deviceInfo.LastSeen = DateTime.Now;

                                // 使用 ConcurrentDictionary 原子操作更新或添加设备
                                var existingDevice = _discoveredDevices.GetOrAdd(deviceInfo.DeviceId, deviceInfo);
                                if (!ReferenceEquals(existingDevice, deviceInfo))
                                {
                                    existingDevice.LastSeen = DateTime.Now;
                                    existingDevice.IpAddress = deviceInfo.IpAddress;
                                }
                                else
                                {
                                    OnDeviceDiscovered?.Invoke(deviceInfo);
                                }

                                // 检查设备数量上限
                                if (_discoveredDevices.Count > MaxDiscoveredDevices)
                                {
                                    CleanupExpiredDevices();
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
                            Debug.WriteLine($"解析设备信息失败: {ex.Message}");
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
                        Debug.WriteLine($"接收广播消息失败: {ex.Message}");
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
            Debug.WriteLine($"监听广播线程异常: {ex.Message}");
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
                    var data = Encoding.UTF8.GetBytes(message);

                    await _udpClient.SendAsync(data, data.Length, _broadcastEndPoint).ConfigureAwait(false);

                    // 重置失败计数
                    _failedBroadcasts = 0;

                    CleanupExpiredDevices();
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
                            _currentBroadcastIntervalMs = Math.Min(_currentBroadcastIntervalMs * 2, MaxBroadcastIntervalMs);
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
            Debug.WriteLine($"发送广播线程异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 调整广播间隔
    /// </summary>
    private void AdjustBroadcastInterval()
    {
        Interlocked.Increment(ref _broadcastCount);

            // 快速发现阶段
        if (_broadcastCount <= FastDiscoveryCount)
        {
            _currentBroadcastIntervalMs = InitialBroadcastIntervalMs;
        }
            // 稳定阶段
        else
        {
            int deviceCount = _discoveredDevices.Count;

            if (deviceCount == 0)
            {
                    // 没有发现设备，保持较短间隔
                _currentBroadcastIntervalMs = Math.Min(StableBroadcastIntervalMs, _currentBroadcastIntervalMs);
            }
            else if (deviceCount <= 1)
            {
                _currentBroadcastIntervalMs = StableBroadcastIntervalMs;
            }
            else
            {
                    // 发现多个设备，使用较长间隔
                _currentBroadcastIntervalMs = Math.Min(StableBroadcastIntervalMs * 2, MaxBroadcastIntervalMs);
            }
        }
    }

    /// <summary>
    /// 带节流的发送回应消息
    /// </summary>
    private async Task SendResponseWithThrottlingAsync(IPEndPoint remoteEndPoint)
    {
        var deviceKey = remoteEndPoint.Address.ToString();
        var now = DateTime.Now;

        // 原子检查并更新节流时间
        if (_lastResponseTimes.TryGetValue(deviceKey, out var lastResponseTime))
        {
            var timeSinceLastResponse = now - lastResponseTime;
            if (timeSinceLastResponse.TotalMilliseconds < MinResponseIntervalMs)
            {
                    // 未到最小回应间隔，跳过
                return;
            }
        }

        _lastResponseTimes[deviceKey] = now;
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
            var data = Encoding.UTF8.GetBytes(message);

            await _udpClient.SendAsync(data, data.Length, remoteEndPoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"发送回应消息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理过期设备
    /// </summary>
    private void CleanupExpiredDevices()
    {
        var now = DateTime.Now;
        var cutoffTime = now.AddSeconds(-DeviceExpirySeconds);

        foreach (var kvp in _discoveredDevices)
        {
            if (kvp.Value.LastSeen < cutoffTime)
            {
                if (_discoveredDevices.TryRemove(kvp.Key, out var device))
                {
                    OnDeviceRemoved?.Invoke(device);
                }
            }
        }

        // 清理过期的回应时间记录
        foreach (var kvp in _lastResponseTimes)
        {
            if ((now - kvp.Value).TotalSeconds > DeviceExpirySeconds)
            {
                _lastResponseTimes.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// 获取当前发现的设备列表
    /// </summary>
    public List<DeviceInfo> GetDiscoveredDevices()
    {
        return _discoveredDevices.Values.ToList();
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
