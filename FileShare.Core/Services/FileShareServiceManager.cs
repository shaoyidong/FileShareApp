using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.NetworkInformation;
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
    private readonly ILogger<FileShareServiceManager> _logger;

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
    /// <param name="loggerFactory">日志工厂（可选，不传则使用 NullLogger）</param>
    public FileShareServiceManager(
        IPlatformDirectoryService directoryService,
        IDatabaseService databaseService,
        string deviceName, DeviceType deviceType, int discoveryPort = 5236, int transferPort = 5237,
        ILoggerFactory? loggerFactory = null)
    {
        _databaseService = databaseService;
        _logger = loggerFactory?.CreateLogger<FileShareServiceManager>() ?? NullLogger<FileShareServiceManager>.Instance;

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
        _discoveryService = new UdpDiscoveryService(_localDevice, loggerFactory?.CreateLogger<UdpDiscoveryService>());
        _fileTransferService = new TcpFileTransferService(directoryService, transferPort, loggerFactory?.CreateLogger<TcpFileTransferService>());

        // 注册事件处理
        _discoveryService.OnDeviceDiscovered += device => OnDeviceDiscovered?.Invoke(device);
        _discoveryService.OnDeviceRemoved += device => OnDeviceRemoved?.Invoke(device);
        _fileTransferService.OnTransferRequestSendAndReceive += info => OnTransferRequestSendAndReceive?.Invoke(info);
        _fileTransferService.OnTransferProgressUpdated += info => OnTransferProgressUpdated?.Invoke(info);
        _fileTransferService.OnTransferCompleted += (info, message) =>
        {
            if (info.Status == TransferStatus.Completed && info.ReceiverId == _localDevice.DeviceId)
            {
                //存储接收记录
                //查询发送设备名
                string? senderDeviceName = _discoveryService.GetDiscoveredDevices()?.FirstOrDefault(d=>d.DeviceId == info.SenderId)?.DeviceName;
                var receiveHistory = new ReceiveHistoryEntity()
                {
                    SenderId = info.SenderId,
                    SenderDeviceName = senderDeviceName,
                    FileName = info.FileName,
                    FileSize = info.FileSize,
                    SavePath = info.SavePath??string.Empty,
                    CreatedAt = DateTime.UtcNow,
                };
                _databaseService.AddSingleReceiveHistoryAsync(receiveHistory);
            }
            OnTransferCompleted?.Invoke(info, message);
        };
    }

    /// <summary>
    /// 获取本地IP地址（多网卡支持：优先选择有网关的物理网卡 IPv4 地址）
    /// </summary>
    private string GetLocalIpAddress()
    {
        var candidates = new List<(IPAddress Address, int Rank)>();

        try
        {
            // 优先使用 NetworkInterface 枚举（比 Dns.GetHostEntry 更可靠，可识别多网卡/虚拟网卡）
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;                

                var props = nic.GetIPProperties();
                // 有网关的网卡更可能是真实的、可达外网的物理网卡，优先选择
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                int rank = hasGateway ? 0 : 1;

                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr.Address))
                    {
                        candidates.Add((addr.Address, rank));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举本地IP地址失败");
        }

        if (candidates.Count == 0)
        {
            // 回退：使用 Dns.GetHostEntry
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        _logger.LogWarning("NetworkInterface 枚举无结果，回退到 Dns.GetHostEntry: {Ip}", ip);
                        return ip.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dns.GetHostEntry 获取本地IP失败");
            }

            _logger.LogWarning("未找到可用的本地IPv4地址，回退到 127.0.0.1");
            return "127.0.0.1";
        }

        var best = candidates.OrderBy(c => c.Rank).First().Address;
        _logger.LogDebug("选择本地IP: {Ip} (共 {Count} 个候选)", best, candidates.Count);
        return best.ToString();
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
    public async Task RefreshDevicesAsync()
    {
        await _discoveryService.SendDiscoveryPacketAsync().ConfigureAwait(false);
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

    public Task<bool> DeleteSingleReceiveHistoryAsync(int id)
    {
        return _databaseService.DeleteSingleReceiveHistoryAsync(id);
    }

    public Task<bool> ClearReceiveHistoryAsync()
    {
        return _databaseService.ClearReceiveHistoryAsync();
    }

    public Task<IEnumerable<ReceiveHistoryEntity>> GetAllReceiveHistoryAsync()
    {
        return _databaseService.GetAllReceiveHistoryAsync();
    }

    /// <summary>
    /// 异步释放资源（推荐使用，避免同步阻塞）
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopServicesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "异步停止服务时出错");
        }

        _discoveryService.Dispose();
        _fileTransferService.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 同步释放资源（作为 IAsyncDisposable 不可用时的回退）
    /// </summary>
    public void Dispose()
    {
        // 在线程池中执行异步操作，避免在 UI 上下文中死锁
        Task.Run(async () => await StopServicesAsync().ConfigureAwait(false))
            .GetAwaiter()
            .GetResult();
        _discoveryService.Dispose();
        _fileTransferService.Dispose();
        GC.SuppressFinalize(this);
    }
}
