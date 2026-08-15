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

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// UDP设备发现服务（优化版：默认仅用于快速初探，减少背景流量）
/// </summary>
public class UdpMulticastDiscoveryService : IDeviceDiscoveryService
{        
    private const string DiscoveryMessage = "FSDISCOVER";
    private const string GoodbyeMessage = "FSGOODBYE";

    // 组播间隔配置
    private const int MaxBroadcastIntervalMs = 15000; // 最大组播间隔
    private const int StableBroadcastIntervalMs = 10000; // 稳定状态组播间隔

    // 设备过期时间
    private const int DeviceExpirySeconds = 45;

    // 回应节流
    private const int MinResponseIntervalMs = 500;

    private readonly string _multicastAddress;
    private readonly int _multicastPort;
    private readonly DeviceInfo _localDevice;
    private readonly ILogger<UdpMulticastDiscoveryService> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly IPEndPoint _multicastEndPoint;
    private readonly IPAddress _localIp;
    private readonly ConcurrentDictionary<string, DateTime> _lastResponseTimes = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    // 后台循环任务的控制
    private Task? _announceLoopTask;

    public event Action<DeviceInfo>? OnDeviceDiscovered;

    public event Action<string>? OnDeviceRemoved;

    public UdpMulticastDiscoveryService(DeviceInfo localDevice, string? udpMulticastAddress, int udpMulticastPort, ILoggerFactory? loggerFactory = null)
    {
        _localDevice = localDevice;
        _logger = loggerFactory?.CreateLogger<UdpMulticastDiscoveryService>() ?? NullLogger<UdpMulticastDiscoveryService>.Instance;
        _localIp = ParseLocalIp(localDevice.IpAddress);
        _multicastAddress = udpMulticastAddress ?? "224.0.0.167";
        _multicastPort = udpMulticastPort;
        _multicastEndPoint = new IPEndPoint(IPAddress.Parse(_multicastAddress), _multicastPort);
    }

    /// <summary>
    /// 启动服务：绑定端口，开始监听，并执行快速发现
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_isRunning || _isDisposed)
            return false;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _multicastPort));
            _udpClient.JoinMulticastGroup(IPAddress.Parse(_multicastAddress));
            try { _udpClient.Ttl = 1; } catch { /* 部分平台不支持，忽略 */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加入组播组 {Group}:{Port} 失败", _multicastAddress, _multicastPort);
            _udpClient?.Dispose();
            _udpClient = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        _logger.LogInformation("UDP组播发现已启动，组播地址 {Group}:{Port}", _multicastAddress, _multicastPort);

        // 初始公告
        _ = Task.Run(async () =>
        {
            await SendAnnouncementAsync(_cts.Token).ConfigureAwait(false);
        });

        // 启动监听任务
        _ = Task.Run(() => ListenAsync(_cts.Token));

        _isRunning = true;
        return true;
    }

    /// <summary>
    /// 停止服务，发送Goodbye（可选）并释放资源
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning || _isDisposed)
            return;

        _isRunning = false;
        _cts?.Cancel();

        try { await SendGoodbyeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }

        if (_announceLoopTask != null)
        {
            try { await _announceLoopTask.ConfigureAwait(false); } catch { }
            _announceLoopTask = null;
        }

        await Task.Delay(50).ConfigureAwait(false);

        ReleaseResources();
        _logger.LogInformation("UDP发现服务已停止");
    }

    public async Task SendServiceQueryAsync()
    {
        await SendAnnouncementAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task StartAnnounceLoopAsync()
    {
        if (!_isRunning || _isDisposed)
            return;

        // 如果已有周期性任务则无需重复启动
        if (_announceLoopTask != null && !_announceLoopTask.IsCompleted)
            return;

        _announceLoopTask = Task.Run(() => AnnounceLoopAsync(_cts?.Token ?? CancellationToken.None));
    }

    /// <summary>
    /// 监听组播并回应
    /// </summary>
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested && !_isDisposed)
                    _logger.LogWarning(ex, "接收组播失败");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (result.RemoteEndPoint.Address.Equals(_localIp))
                continue;

            var msg = Encoding.UTF8.GetString(result.Buffer);
            if (string.IsNullOrEmpty(msg))
                continue;

            try
            {
                HandleMessage(msg, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "处理组播报文失败");
            }
        }
    }

    /// <summary>
    /// 后台周期性组播
    /// </summary>
    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        int interval = StableBroadcastIntervalMs;

        while (!cancellationToken.IsCancellationRequested && !_isDisposed && _udpClient != null)
        {
            try
            {
                await SendAnnouncementAsync(cancellationToken).ConfigureAwait(false);

                // 动态调整间隔：根据发现的设备数量
                int deviceCount = _lastResponseTimes.Count;
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
                _logger.LogWarning(ex, "周期性组播失败");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async void HandleMessage(string msg, IPEndPoint remoteIp)
    {
        // ---- 处理下线消息 ----
        if (msg.StartsWith(GoodbyeMessage, StringComparison.Ordinal))
        {
            var json = msg.Substring(GoodbyeMessage.Length);
            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(json, SourceGenerationContext.Default.DeviceInfo);
            if (deviceInfo != null &&
                !string.IsNullOrEmpty(deviceInfo.DeviceId) &&
                deviceInfo.DeviceId != _localDevice.DeviceId)
            {
                OnDeviceRemoved?.Invoke(deviceInfo.DeviceId);
            }
        }
        else if (msg.StartsWith(DiscoveryMessage, StringComparison.Ordinal))
        {
            // 解析设备信息
            var json = msg.Substring(DiscoveryMessage.Length);
            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(json, SourceGenerationContext.Default.DeviceInfo);

            if (deviceInfo != null
                && !string.IsNullOrEmpty(deviceInfo.DeviceId)
                && deviceInfo.DeviceId != _localDevice.DeviceId
                && !string.IsNullOrEmpty(deviceInfo.DeviceName))
            {
                deviceInfo.IpAddress = remoteIp.Address.ToString();
                deviceInfo.LastSeen = DateTime.Now;

                OnDeviceDiscovered?.Invoke(deviceInfo);

                await SendResponseWithThrottlingAsync(remoteIp).ConfigureAwait(false);
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
        CleanupExpiredResponseTimes();
    }

    private async Task SendAnnouncementAsync(CancellationToken cancellationToken)
    {
        if (!_isRunning || _isDisposed || _udpClient == null) 
            return;

        try
        {
            var data = BuildDiscoveryPacket();
            await SendMulticastAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发送发现数据包失败");
        }
    }

    /// <summary>
    /// 发送下线通知（Goodbye），告知其他设备本机即将离线。
    /// </summary>
    private async Task SendGoodbyeAsync()
    {
        if (!_isRunning || _isDisposed || _udpClient == null) return;
        try
        {
            var data = BuildGoodbyePacket();
            await SendMulticastAsync(data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
        }
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

    private byte[] BuildGoodbyePacket()
    {
        var deviceJson = JsonSerializer.Serialize(_localDevice, SourceGenerationContext.Default.DeviceInfo);
        var message = GoodbyeMessage + deviceJson;
        return Encoding.UTF8.GetBytes(message);
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
    private void CleanupExpiredResponseTimes()
    {
        var now = DateTime.Now;
        var cutoff = now.AddSeconds(-DeviceExpirySeconds);

        // 清理过期的回应时间记录
        foreach (var kvp in _lastResponseTimes)
        {
            if ((now - kvp.Value).TotalSeconds > DeviceExpirySeconds)
                _lastResponseTimes.TryRemove(kvp.Key, out _);
        }
    }

    private async Task SendLockedAsync(byte[] data, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        if (_isDisposed || _udpClient == null)
            return;
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

    private async Task SendMulticastAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (_isDisposed || _udpClient == null)
            return;
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isDisposed && _udpClient != null)
                await _udpClient.SendAsync(data, data.Length, _multicastEndPoint).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static IPAddress ParseLocalIp(string ipAddress)
    {
        return IPAddress.TryParse(ipAddress, out var ip) ? ip : IPAddress.None;
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
                _cts?.Cancel();
                // 释放托管资源
                ReleaseResources();
                _cts?.Dispose();
                _sendLock.Dispose();
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
    ~UdpMulticastDiscoveryService()
    {
        Dispose(false);
    }
}