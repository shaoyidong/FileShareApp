using FileShare.Core.Models;
using FileShare.Core.Network;

namespace FileShare.Core.Services;

/// <summary>
/// 文件共享服务管理器，整合设备发现和文件传输功能
/// </summary>
public class FileShareServiceManager : IDisposable
{
    private readonly UdpDiscoveryService _discoveryService;
    private readonly TcpFileTransferService _fileTransferService;
    private readonly DeviceInfo _localDevice;
    
    /// <summary>
    /// 设备列表更新事件
    /// </summary>
    public event Action<List<DeviceInfo>>? OnDevicesUpdated;
    
    /// <summary>
    /// 接收到文件传输请求事件
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
    
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="deviceName">本地设备名称</param>
    /// <param name="deviceType">设备类型</param>
    /// <param name="discoveryPort">设备发现端口</param>
    /// <param name="transferPort">文件传输端口</param>
    public FileShareServiceManager(string deviceName, DeviceType deviceType, int discoveryPort = 5236, int transferPort = 5237)
    {
        // 生成设备ID
        var deviceId = GetOrCreateDeviceId();
        
        // 创建本地设备信息
        _localDevice = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            DeviceType = deviceType,
            Port = transferPort
        };
        
        // 初始化服务
        _discoveryService = new UdpDiscoveryService(_localDevice.DeviceId, _localDevice.DeviceName, _localDevice.DeviceType);
        _fileTransferService = new TcpFileTransferService(transferPort);
        
        // 注册事件处理
        _discoveryService.OnDeviceDiscovered += device => OnDevicesUpdated?.Invoke(new List<DeviceInfo> { device });
        _fileTransferService.OnTransferRequestReceived += info => OnTransferRequestReceived?.Invoke(info);
        _fileTransferService.OnTransferProgressUpdated += (transferId, transferredSize, totalSize) => OnTransferProgressUpdated?.Invoke(transferId, transferredSize, totalSize);
        _fileTransferService.OnTransferCompleted += (transferId, success, message) => OnTransferCompleted?.Invoke(transferId, success, message);
    }
    
    /// <summary>
    /// 获取或创建设备ID
    /// </summary>
    private string GetOrCreateDeviceId()
    {
        // 在实际应用中，应该从持久化存储中读取设备ID，如果不存在则生成新的
        return Guid.NewGuid().ToString();
    }
    
    /// <summary>
    /// 启动服务
    /// </summary>
    public async Task StartServicesAsync()
    {
        await _discoveryService.StartAsync();
        await _fileTransferService.StartAsync();
    }
    
    /// <summary>
    /// 停止服务
    /// </summary>
    public async Task StopServicesAsync()
    {
        await _discoveryService.StopAsync();
        await _fileTransferService.StopAsync();
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
        return await _fileTransferService.SendFileAsync(filePath, targetDevice, _localDevice.DeviceId);
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
    /// <param name="transferInfo">传输信息</param>
    /// <param name="accept">是否接受</param>
    public void HandleTransferRequest(FileTransferInfo transferInfo, bool accept)
    {
        // 将用户的选择传递给文件传输服务
        _fileTransferService.HandleTransferRequest(transferInfo.TransferId, accept);
        
        // 如果拒绝请求，更新状态
        if (!accept)
        {
            transferInfo.Status = TransferStatus.Cancelled;
            OnTransferProgressUpdated?.Invoke(transferInfo.TransferId, 0, 0);
        }
    }
    
    public void Dispose()
    {
        // 在线程池中执行异步操作，避免死锁
        Task.Run(async () => await StopServicesAsync().ConfigureAwait(false))
            .GetAwaiter()
            .GetResult();
        _discoveryService.Dispose();
        _fileTransferService.Dispose();
    }
}