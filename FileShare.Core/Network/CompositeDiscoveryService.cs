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
        private readonly IDeviceDiscoveryService _udpMulticast;
        private readonly IDeviceDiscoveryService _udpBroadcast;
        private readonly DiscoveryOptions _discoveryOptions;
        private readonly IDeviceDiscoveryService _mdns;
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

            _mdns = new MdnsService(localDevice, loggerFactory);
            _mdns.OnDeviceDiscovered += MergeDevice;
            _mdns.OnDeviceRemoved += RemoveDevice;

            _udpMulticast = new UdpMulticastDiscoveryService(localDevice,discoveryOptions.UdpMulticastAddress, discoveryOptions.UdpMulticastPort, loggerFactory);
            _udpMulticast.OnDeviceDiscovered += MergeDevice;
            _udpMulticast.OnDeviceRemoved += RemoveDevice;

            _udpBroadcast = new UdpBroadcastDiscoveryService(localDevice, discoveryOptions.UdpBroadcastPort, loggerFactory);
            _udpBroadcast.OnDeviceDiscovered += MergeDevice;
            _udpBroadcast.OnDeviceRemoved += RemoveDevice;
        }

        private void MergeDevice(DeviceInfo device)
        {
            if (_localDevice.DeviceId == device.DeviceId)
            {
                return;
            }

            // 如果已经存在，则直接更新已有设备信息
            if (_devices.TryGetValue(device.DeviceId, out var existing))
            {
                existing.LastSeen = DateTime.Now;
                existing.IpAddress = device.IpAddress;
                existing.DeviceName = device.DeviceName;
                existing.DeviceType = device.DeviceType;
                existing.SupportsTls = device.SupportsTls;
                return;
            }
          
            if (_devices.Count >= MaxDiscoveredDevices)
            {
                CleanupExpiredDevices(); 
                
                if (_devices.Count >= MaxDiscoveredDevices)
                {
                    _logger.LogWarning("已达到最大发现设备数，无法添加新设备: {DeviceId}", device.DeviceId);
                    return;
                }
            }

            // 尝试添加，
            if (_devices.TryAdd(device.DeviceId, device))
            {
                OnDeviceDiscovered?.Invoke(device);
            }
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

            var mdnsStartTask = Task.Run(async () =>
            {
                _mdnsSuccess = await _mdns.StartAsync();
            });
            var udpMulticastStartTask = Task.Run(async () =>
            {
                _udpMulticastSuccess = await _udpMulticast.StartAsync();
            });
            var udpBroadcastStartTask = Task.Run(async () =>
            {
                _udpBroadcastSuccess = await _udpBroadcast.StartAsync();
            });
            await Task.WhenAll(mdnsStartTask, udpMulticastStartTask, udpBroadcastStartTask);

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

            await Task.WhenAll(_mdns.StopAsync(), _udpMulticast.StopAsync(), _udpBroadcast.StopAsync());
        }

        private void Stop()
        {
            _isRunning = false;
            _mdns.OnDeviceDiscovered -= MergeDevice;
            _udpMulticast.OnDeviceDiscovered -= MergeDevice;
            _udpBroadcast.OnDeviceDiscovered -= MergeDevice;
            _mdns.OnDeviceRemoved -= RemoveDevice;
            _udpMulticast.OnDeviceRemoved -= RemoveDevice;
            _udpBroadcast.OnDeviceRemoved -= RemoveDevice;
            _cts?.Cancel();
        }

        public async Task SendServiceQueryAsync()
        {
            await Task.WhenAll(_mdns.SendServiceQueryAsync(), _udpMulticast.SendServiceQueryAsync(), _udpBroadcast.SendServiceQueryAsync());
        }

        public async Task StartAnnounceLoopAsync()
        {
            if (!_isRunning || _disposedValue)
                return;            

            if (_mdnsSuccess)
            {
                await _mdns.StartAnnounceLoopAsync();
            }
            else if (_udpMulticastSuccess) 
            {
                await _udpMulticast.StartAnnounceLoopAsync();
            }
            else if(_udpBroadcastSuccess)
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
