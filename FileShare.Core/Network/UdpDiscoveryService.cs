using FileShare.Core.Common;
using FileShare.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FileShare.Core.Network;

/// <summary>
/// UDP设备发现服务（优化版：默认仅用于快速初探，减少背景流量）
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
    private const int DeviceExpirySeconds = 20;

    // 回应节流
    private const int MinResponseIntervalMs = 500;

    // 最大设备数
    private const int MaxDiscoveredDevices = 200;

    private readonly ConcurrentDictionary<string, DateTime> _lastResponseTimes = new();
    private readonly ConcurrentDictionary<string, DeviceInfo> _discoveredDevices = new();

    // 网络拥塞检测
    private int _failedBroadcasts = 0; // 失败的广播次数
    private const int MaxFailedBroadcasts = 3; // 最大失败次数阈值

    private UdpClient? _udpClient;
    private readonly IPEndPoint _broadcastEndPoint;
    private readonly CancellationTokenSource _cts;
    private readonly DeviceInfo _localDevice;
    private readonly ILogger<UdpDiscoveryService> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly bool _enablePeriodicBroadcast;  // 是否允许后台周期性广播
    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private volatile bool _fallbackMode;             // 降级模式标志

    // 后台循环任务的控制
    private Task? _periodicTask;    

    /// <summary>
    /// 发现设备时触发
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;

    /// <summary>
    /// 设备离线时触发
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceRemoved;

    /// <summary>
    /// 构造UDP发现服务
    /// </summary>
    /// <param name="localDevice">本机设备信息</param>
    /// <param name="logger">日志</param>
    /// <param name="enablePeriodicBroadcast">是否启用后台周期性广播（默认false，仅快速探测）</param>
    public UdpDiscoveryService(DeviceInfo localDevice, ILogger<UdpDiscoveryService>? logger = null, bool enablePeriodicBroadcast = false)
    {
        _localDevice = localDevice;
        _logger = logger ?? NullLogger<UdpDiscoveryService>.Instance;
        _enablePeriodicBroadcast = enablePeriodicBroadcast;
        _udpClient = new UdpClient();
        _udpClient.EnableBroadcast = true;
        _broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        _cts = new CancellationTokenSource();
    }    

    /// <summary>
    /// 启动服务：绑定端口，开始监听，并执行快速发现（连续发送几次广播）
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning || _isDisposed) return;

        _isRunning = true;
        _fallbackMode = false;
        _failedBroadcasts = 0;

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
            _logger.LogError(ex, "绑定UDP端口 {Port} 失败，发现服务不可用", DiscoveryPort);
            _isRunning = false;
            return;
        }

        _logger.LogInformation("UDP发现服务已启动，端口 {Port} (周期性广播: {Periodic})", DiscoveryPort, _enablePeriodicBroadcast);

        // 启动监听任务（始终运行）
        _ = Task.Run(() => ListenForBroadcastsAsync(_cts.Token));

        _ = Task.Run(() => CleanupExpiredDevicesLoopAsync(_cts.Token));

        // 快速发现：连续发送若干次广播，快速发现周边设备
        _ = Task.Run(() => SendFastBroadcastsAsync(FastDiscoveryCount, InitialBroadcastIntervalMs, _cts.Token));

        // 如果启用了后台周期性广播，则启动循环任务
        if (_enablePeriodicBroadcast)
        {
            _periodicTask = Task.Run(() => SendPeriodicBroadcastsAsync(_cts.Token));
        }
        // 否则不启动周期性发送，仅靠快速探测和外部手动触发
    }   

    /// <summary>
    /// 停止服务，发送Goodbye（可选）并释放资源
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning || _isDisposed) return;
        _isRunning = false;
        _cts.Cancel();

        if (_periodicTask != null)
        {
            try { await _periodicTask.ConfigureAwait(false); } catch { }
            _periodicTask = null;
        }

        await Task.Delay(50).ConfigureAwait(false);
        ReleaseResources();
        _logger.LogInformation("UDP发现服务已停止");
    }

    /// <summary>
    /// 快速连续发送若干次广播
    /// </summary>
    private async Task SendFastBroadcastsAsync(int count, int intervalMs, CancellationToken cancellationToken)
    {
        if (_udpClient == null) return;
        for (int i = 0; i < count && !cancellationToken.IsCancellationRequested && !_isDisposed; i++)
        {
            try
            {
                var data = BuildDiscoveryPacket();
                await SendToAllBroadcastsAsync(data, cancellationToken).ConfigureAwait(false);
                if (i < count - 1)
                    await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "快速发送广播失败 (第{Count}次)", i + 1);
            }
        }
    }


    /// <summary>
    /// 异步发送发现数据包（手动刷新时调用）
    /// </summary>
    public async Task SendDiscoveryPacketAsync()
    {
        // 仅在服务运行时发送；修复之前的逻辑错误（原为 _isRunning 时直接返回，导致手动刷新失效）
        if (!_isRunning || _isDisposed || _udpClient == null) return;

        try
        {
            var data = BuildDiscoveryPacket();
            await SendToAllBroadcastsAsync(data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送发现数据包失败");
        }
    }


    /// <summary>
    /// 启用降级模式：当mDNS不可用时，开启后台周期性广播（5~15秒一次）
    /// </summary>
    public void EnableFallbackMode()
    {
        if (_isDisposed || !_isRunning) return;
        if (_fallbackMode) return;

        _fallbackMode = true;
        _logger.LogWarning("UDP发现服务切换到降级模式，开始周期性广播");

        // 如果已有周期性任务则无需重复启动
        if (_periodicTask != null && !_periodicTask.IsCompleted) return;

        _periodicTask = Task.Run(() => SendPeriodicBroadcastsAsync(_cts.Token));
    }   

    /// <summary>
    /// 监听广播并回应
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

                                var existing = _discoveredDevices.GetOrAdd(deviceInfo.DeviceId, deviceInfo);
                                if (!ReferenceEquals(existing, deviceInfo))
                                {
                                    existing.LastSeen = DateTime.Now;
                                    existing.IpAddress = deviceInfo.IpAddress;
                                }
                                else
                                {
                                    OnDeviceDiscovered?.Invoke(deviceInfo);
                                }

                                // 检查设备数量上限
                                if (_discoveredDevices.Count > MaxDiscoveredDevices)
                                    CleanupExpiredDevices();

                                // 回应对方（节流）
                                if (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
                                    await SendResponseWithThrottlingAsync(result.RemoteEndPoint).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "解析设备信息失败");
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                        _logger.LogWarning(ex, "接收广播失败");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "监听线程异常");
        }
    }

    /// <summary>
    /// 后台周期性广播（仅在降级模式或显式启用时运行）
    /// </summary>
    private async Task SendPeriodicBroadcastsAsync(CancellationToken cancellationToken)
    {
        int interval = StableBroadcastIntervalMs;
        _failedBroadcasts = 0;

        while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
        {
            try
            {
                var data = BuildDiscoveryPacket();
                await SendToAllBroadcastsAsync(data, cancellationToken).ConfigureAwait(false);

                    // 重置失败计数
                _failedBroadcasts = 0;               

                // 动态调整间隔：根据发现的设备数量
                int deviceCount = _discoveredDevices.Count;
                if (deviceCount == 0)
                    interval = Math.Min(StableBroadcastIntervalMs, interval);
                else if (deviceCount <= 1)
                    interval = StableBroadcastIntervalMs;
                else
                    interval = Math.Min(StableBroadcastIntervalMs * 2, MaxBroadcastIntervalMs);

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                {
                    _logger.LogWarning(ex, "周期性广播失败");
                    _failedBroadcasts++;

                        // 网络拥塞检测
                        if (_failedBroadcasts >= MaxFailedBroadcasts)
                    {
                        interval = Math.Min(interval * 2, MaxBroadcastIntervalMs);
                        _logger.LogWarning("广播连续失败，增加间隔至 {Interval}ms", interval);
                        _failedBroadcasts = 0;
                    }
                }
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupExpiredDevicesLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            try
            {                
                CleanupExpiredDevices();
                await Task.Delay(StableBroadcastIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理过期设备失败");
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

        if (_lastResponseTimes.TryGetValue(deviceKey, out var last))
        {
            if ((now - last).TotalMilliseconds < MinResponseIntervalMs)
                return;
        }
        _lastResponseTimes[deviceKey] = now;
        await SendResponseAsync(remoteEndPoint).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送回应消息（单播到已发现的对端）
    /// </summary>
    private async Task SendResponseAsync(IPEndPoint remoteEndPoint)
    {
        if (_isDisposed || _udpClient == null) return;
        try
        {
            var data = BuildDiscoveryPacket();
            await SendLockedAsync(data, remoteEndPoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送回应失败");
        }
    }

    /// <summary>
    /// 清理过期设备
    /// </summary>
    private void CleanupExpiredDevices()
    {
        var now = DateTime.Now;
        var cutoff = now.AddSeconds(-DeviceExpirySeconds);

        foreach (var kvp in _discoveredDevices)
        {
            if (kvp.Value.LastSeen < cutoff)
            {
                if (_discoveredDevices.TryRemove(kvp.Key, out var device))
                    OnDeviceRemoved?.Invoke(device);
            }
        }

        // 清理过期的回应时间记录
        foreach (var kvp in _lastResponseTimes)
        {
            if ((now - kvp.Value).TotalSeconds > DeviceExpirySeconds)
                _lastResponseTimes.TryRemove(kvp.Key, out _);
        }
    }

    public List<DeviceInfo> GetDiscoveredDevices() => _discoveredDevices.Values.ToList();

    /// <summary>
    /// 注册由外部发现机制（如 mDNS）发现的设备，纳入统一的设备表与过期清理。
    /// <para>首次见到该设备时触发 OnDeviceDiscovered；已存在则更新 LastSeen 等字段（不重复触发事件）。</para>
    /// </summary>
    public void RegisterExternalDevice(DeviceInfo deviceInfo)
    {
        if (deviceInfo == null || string.IsNullOrEmpty(deviceInfo.DeviceId)) return;
        if (deviceInfo.DeviceId == _localDevice.DeviceId) return;

        // 检查设备数量上限，避免恶意/异常源耗尽内存
        if (_discoveredDevices.Count > MaxDiscoveredDevices)
            CleanupExpiredDevices();

        var existing = _discoveredDevices.GetOrAdd(deviceInfo.DeviceId, deviceInfo);
        if (!ReferenceEquals(existing, deviceInfo))
        {
            existing.LastSeen = DateTime.Now;
            existing.IpAddress = deviceInfo.IpAddress;
            existing.Port = deviceInfo.Port;
            existing.SupportsTls = deviceInfo.SupportsTls;
            if (!string.IsNullOrEmpty(deviceInfo.DeviceName))
                existing.DeviceName = deviceInfo.DeviceName;
        }
        else
        {
            OnDeviceDiscovered?.Invoke(deviceInfo);
        }
    }

    public void RemoveExternalDevice(DeviceInfo deviceInfo)
    {
        if (deviceInfo == null || string.IsNullOrEmpty(deviceInfo.DeviceId)) return;
        if (deviceInfo.DeviceId == _localDevice.DeviceId) return;

        if (_discoveredDevices.TryRemove(deviceInfo.DeviceId, out var device))
            OnDeviceRemoved?.Invoke(device);
    }

    /// <summary>
    /// 构造发现数据包（前缀 + 本地设备 JSON）
    /// </summary>
    private byte[] BuildDiscoveryPacket()
    {
        var deviceJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
        var message = DiscoveryMessage + deviceJson;
        return Encoding.UTF8.GetBytes(message);
    }

    /// <summary>
    /// 向所有活跃网卡的子网广播地址发送数据包（多网卡支持）
    /// </summary>
    private async Task SendToAllBroadcastsAsync(byte[] data, CancellationToken cancellationToken)
    {
        var endpoints = GetBroadcastEndpoints();
        foreach (var endpoint in endpoints)
            await SendLockedAsync(data, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLockedAsync(byte[] data, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (_isDisposed || _udpClient == null) return;
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isDisposed && _udpClient != null)
                await _udpClient.SendAsync(data, data.Length, endpoint).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 枚举所有活跃网卡的子网定向广播地址，并附加有限广播作为兜底
    /// </summary>
    private List<IPEndPoint> GetBroadcastEndpoints()
    {
        var endpoints = new List<IPEndPoint>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = addr.IPv4Mask;
                    if (mask == null) continue;

                    var broadcast = GetDirectedBroadcast(addr.Address, mask);
                    if (broadcast != null)
                        endpoints.Add(new IPEndPoint(broadcast, DiscoveryPort));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举网卡广播地址失败");
        }

        // 始终包含有限广播作为兜底（部分平台/驱动不发送定向广播）
        endpoints.Add(_broadcastEndPoint);
        return endpoints.Distinct().ToList();
    }

    /// <summary>
    /// 根据IP和子网掩码计算定向广播地址 (ip | ~mask)
    /// </summary>
    private static IPAddress? GetDirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var addrBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addrBytes.Length != 4 || maskBytes.Length != 4) return null;
        var bcast = new byte[4];
        for (int i = 0; i < 4; i++)
            bcast[i] = (byte)(addrBytes[i] | (byte)~maskBytes[i]);
        return new IPAddress(bcast);
    }

    private void ReleaseResources()
    {
        if (_udpClient != null)
        {
            try { _udpClient.Close(); _udpClient.Dispose(); } catch { }
            _udpClient = null;
        }
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
        _sendLock.Dispose();

                // 释放CancellationTokenSource
        _cts.Dispose();
            }

            _isDisposed = true;
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
    /// 析构函数
    /// </summary>
    ~UdpDiscoveryService()
    {
        Dispose(false);
    }
}