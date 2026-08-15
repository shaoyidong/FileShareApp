using FileShare.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Network
{
    public interface IDeviceDiscoveryService: IDisposable
    {
        Task<bool> StartAsync();
        Task StopAsync();
        event Action<DeviceInfo> OnDeviceDiscovered;
        event Action<string> OnDeviceRemoved;
        Task SendServiceQueryAsync();
        Task StartAnnounceLoopAsync();
    }
}
