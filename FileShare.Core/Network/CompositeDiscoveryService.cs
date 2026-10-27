using FileShare.Core.Models;
using FileShare.Core.Network.Discovery;
using FileShare.Core.Network.Mdns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Network
{
    public class CompositeDiscoveryService : IDeviceDiscoveryService
    {
        private const int DeviceExpirySeconds = 45;       
        private const int MaxDiscoveredDevices = 200;
        private readonly ILogger<CompositeDiscoveryService> _logger;
        private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
        private readonly IDeviceDiscoveryService? _udpMulticast;
        private readonly IDeviceDiscoveryService? _udpBroadcast;
        private readonly DiscoveryOptions _discoveryOptions;
        private readonly IDeviceDiscoveryService? _mdns;
        private readonly DeviceInfo _localDevice;

        private CancellationTokenSource? _cts;
        private bool _mdnsSuccess;
        private bool _udpMulticastSuccess;
        private bool _udpBroadcastSuccess;
        private bool _isRunning;
        private bool _disposedValue;

        public event Action<DeviceInfo>? OnDeviceDiscovered;
        public event Action<string>? OnDeviceRemoved;

        public CompositeDiscoveryService(DeviceInfo localDevice, DiscoveryOptions? discoveryOptions, ILoggerFactory? loggerFactory = null)
        {
            _logger = loggerFactory?.CreateLogger<CompositeDiscoveryService>() ?? NullLogger<CompositeDiscoveryService>.Instance;
            _localDevice = localDevice;

            if (discoveryOptions == null) 
            { 
                discoveryOptions = new DiscoveryOptions();
            }
            _discoveryOptions = discoveryOptions;

            _mdns = discoveryOptions.EnableMdns ? new MdnsService(localDevice, loggerFactory) : null;
            _udpMulticast = discoveryOptions.EnableUdpMulticast
                ? new UdpMulticastDiscoveryService(
                    localDevice,
                    discoveryOptions.UdpMulticastAddress,
                    discoveryOptions.UdpMulticastPort,
                    loggerFactory,
                    discoveryOptions.NetworkWhitelist,
                    discoveryOptions.NetworkBlacklist)
                : null;
            _udpBroadcast = discoveryOptions.EnableUdpBroadcast
                ? new UdpBroadcastDiscoveryService(
                    localDevice,
                    discoveryOptions.UdpBroadcastPort,
                    loggerFactory,
                    discoveryOptions.NetworkWhitelist,
                    discoveryOptions.NetworkBlacklist)
                : null;

            if (_mdns is null && _udpMulticast is null && _udpBroadcast is null)
            {
                _logger.LogWarning("所有自动发现方式都被关闭，只能通过「IP 直连」或「网段扫描」添加设备");
            }

            // 统一通过 Subscribe/Unsubscribe 管理，避免 StopAsync 之后再 StartAsync 时事件链丢失
            Subscribe();
        }

        private bool _subscribed;

        /// <summary>
        /// 订阅子发现服务的事件。可重复调用。
        /// </summary>
        private void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            SubscribeChild(_mdns);
            SubscribeChild(_udpMulticast);
            SubscribeChild(_udpBroadcast);
            _subscribed = true;
        }

        /// <summary>
        /// 取消订阅子发现服务的事件。
        /// </summary>
        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            UnsubscribeChild(_mdns);
            UnsubscribeChild(_udpMulticast);
            UnsubscribeChild(_udpBroadcast);
            _subscribed = false;
        }

        private void SubscribeChild(IDeviceDiscoveryService? service)
        {
            if (service is null)
            {
                return;
            }

            service.OnDeviceDiscovered += OnChildDeviceDiscovered;
            service.OnDeviceRemoved += OnChildDeviceRemoved;
        }

        private void UnsubscribeChild(IDeviceDiscoveryService? service)
        {
            if (service is null)
            {
                return;
            }

            service.OnDeviceDiscovered -= OnChildDeviceDiscovered;
            service.OnDeviceRemoved -= OnChildDeviceRemoved;
        }

        private void OnChildDeviceDiscovered(DeviceInfo device) => MergeDevice(device);

        private void OnChildDeviceRemoved(string deviceId) => RemoveDevice(deviceId);

        /// <summary>
        /// 把外部探测到的设备（手动输入 IP、网段扫描结果）合并进设备列表，与自动发现共用同一套去重/过期逻辑。
        /// </summary>
        /// <returns>是否为新增设备。</returns>
        public bool InjectDevice(DeviceInfo device)
        {
            if (device is null || string.IsNullOrEmpty(device.DeviceId))
            {
                return false;
            }

            return MergeDevice(device);
        }

        private bool MergeDevice(DeviceInfo device)
        {
            if (_localDevice.DeviceId == device.DeviceId)
            {
                return false;
            }

            // 如果已经存在，则直接更新已有设备信息
            if (_devices.TryGetValue(device.DeviceId, out var existing))
            {
                existing.LastSeen = DateTime.Now;
                existing.IpAddress = device.IpAddress;
                existing.DeviceName = device.DeviceName;
                existing.DeviceType = device.DeviceType;
                existing.SupportsTls = device.SupportsTls;
                if (device.Port > 0)
                {
                    existing.Port = device.Port;
                }
                return false;
            }
          
            if (_devices.Count >= MaxDiscoveredDevices)
            {
                CleanupExpiredDevices(); 
                
                if (_devices.Count >= MaxDiscoveredDevices)
                {
                    _logger.LogWarning("已达到最大发现设备数，无法添加新设备: {DeviceId}", device.DeviceId);
                    return false;
                }
            }

            // 尝试添加，
            if (_devices.TryAdd(device.DeviceId, device))
            {
                OnDeviceDiscovered?.Invoke(device);
                return true;
            }

            return false;
        }

        private void RemoveDevice(string deviceId)
        {
            if (_localDevice.DeviceId == deviceId)
            {
                return;
            }

            if (_devices.TryRemove(deviceId, out var device))
            {
                OnDeviceRemoved?.Invoke(deviceId);
            }
        }

        public async Task<bool> StartAsync()
        {
            if (_isRunning)
            {
                _logger.LogInformation("Discovery service is already running.");
                return _isRunning;
            }
              
            if(_disposedValue)
            {
                _logger.LogWarning("Discovery service has been disposed.");
                return false;
            }                

            // 支持 Stop → Start 的循环（例如用户在设置里重启服务），重新挂上子服务事件
            Subscribe();

            var startTasks = new List<Task>();
            if (_mdns is not null)
            {
                startTasks.Add(Task.Run(async () => _mdnsSuccess = await _mdns.StartAsync()));
            }
            if (_udpMulticast is not null)
            {
                startTasks.Add(Task.Run(async () => _udpMulticastSuccess = await _udpMulticast.StartAsync()));
            }
            if (_udpBroadcast is not null)
            {
                startTasks.Add(Task.Run(async () => _udpBroadcastSuccess = await _udpBroadcast.StartAsync()));
            }

            if (startTasks.Count > 0)
            {
                await Task.WhenAll(startTasks);
            }

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => CleanupExpiredDevicesLoopAsync(_cts.Token));

            _isRunning = _mdnsSuccess || _udpMulticastSuccess || _udpBroadcastSuccess;

            if (_isRunning)
            {
                _logger.LogInformation("Discovery service started successfully.");

            }
            else
            {
                _logger.LogWarning("Failed to start any discovery service.");
            }

            return _isRunning;
        }

        public async Task StopAsync()
        {
            if (!_isRunning || _disposedValue) 
                return;

            Stop();

            var stopTasks = new List<Task>();
            if (_mdns is not null)
            {
                stopTasks.Add(_mdns.StopAsync());
            }
            if (_udpMulticast is not null)
            {
                stopTasks.Add(_udpMulticast.StopAsync());
            }
            if (_udpBroadcast is not null)
            {
                stopTasks.Add(_udpBroadcast.StopAsync());
            }

            if (stopTasks.Count > 0)
            {
                await Task.WhenAll(stopTasks);
            }
        }

        private void Stop()
        {
            _isRunning = false;
            Unsubscribe();
            _cts?.Cancel();
        }

        public async Task SendServiceQueryAsync()
        {
            var queryTasks = new List<Task>();
            if (_mdns is not null)
            {
                queryTasks.Add(_mdns.SendServiceQueryAsync());
            }
            if (_udpMulticast is not null)
            {
                queryTasks.Add(_udpMulticast.SendServiceQueryAsync());
            }
            if (_udpBroadcast is not null)
            {
                queryTasks.Add(_udpBroadcast.SendServiceQueryAsync());
            }

            if (queryTasks.Count > 0)
            {
                await Task.WhenAll(queryTasks);
            }
        }

        public async Task StartAnnounceLoopAsync()
        {
            if (!_isRunning || _disposedValue)
                return;            

            if (_mdnsSuccess && _mdns is not null)
            {
                await _mdns.StartAnnounceLoopAsync();
            }
            else if (_udpMulticastSuccess && _udpMulticast is not null) 
            {
                await _udpMulticast.StartAnnounceLoopAsync();
            }
            else if(_udpBroadcastSuccess && _udpBroadcast is not null)
            {
                await _udpBroadcast.StartAnnounceLoopAsync();
            }            
        }

        /// <summary>
        /// 清理过期设备
        /// </summary>
        private void CleanupExpiredDevices()
        {
            var now = DateTime.Now;
            var cutoff = now.AddSeconds(-DeviceExpirySeconds);

            foreach (var kvp in _devices)
            {
                if (kvp.Value.LastSeen < cutoff)
                {
                    if (_devices.TryRemove(kvp.Key, out var device))
                        OnDeviceRemoved?.Invoke(kvp.Key);
                }
            }            
        }

        private async Task CleanupExpiredDevicesLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposedValue)
            {
                try
                {
                    CleanupExpiredDevices();
                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理过期设备失败");
                }
            }
        }

        internal ICollection<DeviceInfo>? GetDiscoveredDevices()
        {
            return _devices?.Values;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)    
                    Stop();
                    _mdns?.Dispose();
                    _udpMulticast?.Dispose();
                    _udpBroadcast?.Dispose();
                    _cts?.Dispose();
                }

                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                _disposedValue = true;
            }
        }

        // // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        // ~CompositeDiscoveryService()
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
}
