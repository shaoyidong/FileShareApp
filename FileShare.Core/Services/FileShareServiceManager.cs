using FileShare.Core.Models;
using FileShare.Core.Network;
using System.Net;
using System.Net.Sockets;

namespace FileShare.Core.Services;

/// <summary>
/// 文件共享服务管理器，整合设备发现和文件传输功能
/// </summary>
public class FileShareServiceManager : IFileShareServiceManager
{
    private readonly UdpDiscoveryService _discoveryService;
    private readonly TcpFileTransferService _fileTransferService;
    private readonly DeviceInfo _localDevice;
    private readonly IDatabaseService _databaseService;
    
    /// <summary>
    /// 设备列表更新事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceDiscovered;
    
    /// <summary>
    /// 设备离线事件
    /// </summary>
    public event Action<DeviceInfo>? OnDeviceRemoved;
    
    /// <summary>
    /// 文件传输请求事件
    /// </summary>
    public event Action<FileTransferInfo>? OnTransferRequestSendAndReceive;
    
    /// <summary>
    /// 传输进度更新事件
    /// </summary>
    public event Action<FileTransferInfo>? OnTransferProgressUpdated;
    
    /// <summary>
    /// 传输完成事件
    /// </summary>
    public event Action<FileTransferInfo, string?>? OnTransferCompleted;
    
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="directoryService">平台目录服务</param>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="deviceName">本地设备名称</param>
    /// <param name="deviceType">设备类型</param>
    /// <param name="discoveryPort">设备发现端口</param>
    /// <param name="transferPort">文件传输端口</param>
    public FileShareServiceManager(
        IPlatformDirectoryService directoryService,
        IDatabaseService databaseService,
        string deviceName, DeviceType deviceType, int discoveryPort = 5236, int transferPort = 5237)
    {
        _databaseService = databaseService;
        
        // 生成设备ID
        var deviceId = GetOrCreateDeviceId();
        
        // 创建本地设备信息
        _localDevice = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            DeviceType = deviceType,
            Port = transferPort,
            IpAddress = GetLocalIpAddress()
        };
        
        // 初始化服务
        _discoveryService = new UdpDiscoveryService(_localDevice);
        _fileTransferService = new TcpFileTransferService(directoryService,transferPort);
        
        // 注册事件处理
        _discoveryService.OnDeviceDiscovered += device => OnDeviceDiscovered?.Invoke(device);
        _discoveryService.OnDeviceRemoved += device => OnDeviceRemoved?.Invoke(device);
        _fileTransferService.OnTransferRequestSendAndReceive += info => OnTransferRequestSendAndReceive?.Invoke(info);
        _fileTransferService.OnTransferProgressUpdated += info => OnTransferProgressUpdated?.Invoke(info);
        _fileTransferService.OnTransferCompleted += (info, message) => OnTransferCompleted?.Invoke(info, message);
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
    /// 获取或创建设备ID
    /// </summary>
    private string GetOrCreateDeviceId()
    {
        return _databaseService.GetOrCreateDeviceId();
    }
    
    /// <summary>
    /// 启动服务
    /// </summary>
    public async Task StartServicesAsync()
    {
        await _discoveryService.StartAsync().ConfigureAwait(false);
        await _fileTransferService.StartAsync().ConfigureAwait(false);
    }
    
    /// <summary>
    /// 停止服务
    /// </summary>
    public async Task StopServicesAsync()
    {
        await _discoveryService.StopAsync().ConfigureAwait(false);
        await _fileTransferService.StopAsync().ConfigureAwait(false);
    }
    
    /// <summary>
    /// 获取本地设备信息
    /// </summary>
    public DeviceInfo GetLocalDeviceInfo()
    {
        return _localDevice;
    }
    
    /// <summary>
    /// 发送文件到目标设备
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="targetDevice">目标设备</param>
    /// <returns>是否发送成功</returns>
    public async Task<bool> SendFileAsync(string filePath, DeviceInfo targetDevice)
    {
        return await _fileTransferService.SendFileAsync(filePath, targetDevice, _localDevice.DeviceId).ConfigureAwait(false);
    }
    
    /// <summary>
    /// 手动刷新设备列表
    /// </summary>
    public void RefreshDevices()
    {
        _discoveryService.SendDiscoveryPacket();
    }
    
    /// <summary>
    /// 处理文件传输请求
    /// </summary>
    /// <param name="transferId">传输ID</param>
    /// <param name="accept">是否接受</param>
    /// <param name="savePath">文件保存路径</param>
    public void HandleTransferRequest(string transferId, bool accept, string? savePath = null)
    {
        // 将用户的选择传递给文件传输服务
        _fileTransferService.HandleTransferRequest(transferId, accept, savePath);
    }
    
    /// <summary>
    /// 取消传输
    /// </summary>
    /// <param name="transferId">传输ID</param>
    public void CancelTransfer(string transferId)
    {
        // 将取消请求传递给文件传输服务
        _fileTransferService.CancelTransfer(transferId);
    }
    
    public void Dispose()
    {
        // 在线程池中执行异步操作，避免死锁
        Task.Run(async () => await StopServicesAsync().ConfigureAwait(false))
            .GetAwaiter()
            .GetResult();
        _discoveryService.Dispose();
        _fileTransferService.Dispose();
        GC.SuppressFinalize(this);
    }
}