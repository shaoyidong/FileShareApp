using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Network;
using FileShare.Core.Network.Discovery;
using FileShare.Core.Network.Mdns;
using FileShare.Core.Network.Tls;
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
    private readonly CompositeDiscoveryService _discoveryService;
    private readonly TcpFileTransferService _fileTransferService;
    private readonly DeviceProbeService _probeService;
    private readonly SubnetScanner _subnetScanner;
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
    public event Action<string>? OnDeviceRemoved;

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
    /// <param name="tlsEnabled">是否启用 TLS 加密传输（可选，启用后对同样启用 TLS 的对端自动升级到 SslStream）</param>
    /// <param name="enableMdns">是否启用 mDNS 发现（可选，作为 UDP 广播的补充，提高受限网络下的发现成功率）</param>
    public FileShareServiceManager(
        IPlatformDirectoryService directoryService,
        IDatabaseService databaseService,
        string deviceName, DeviceType deviceType, int transferPort = 5237,
        DiscoveryOptions? discoveryOptions = null,
        bool tlsEnabled = false,
        ILoggerFactory? loggerFactory = null
        )
    {
        _databaseService = databaseService;
        _logger = loggerFactory?.CreateLogger<FileShareServiceManager>() ?? NullLogger<FileShareServiceManager>.Instance;

        // 生成设备ID
        var deviceId = GetOrCreateDeviceId();

        // 创建本地设备信息（SupportsTls 通过发现协议广播，发送方据此决定是否升级到 SslStream）
        _localDevice = new DeviceInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            DeviceType = deviceType,
            Port = transferPort,
            IpAddress = GetLocalIpAddress(),
            SupportsTls = tlsEnabled
        };

        // 初始化服务
        _discoveryService = new CompositeDiscoveryService(_localDevice, discoveryOptions, loggerFactory);
        _fileTransferService = new TcpFileTransferService(deviceId, directoryService, transferPort, tlsEnabled, loggerFactory);        

        // 手动直连 / 网段扫描（跨子网与广播被拦截场景的兜底方案）
        _probeService = new DeviceProbeService(_localDevice, transferPort, loggerFactory);
        _subnetScanner = new SubnetScanner(_probeService, loggerFactory);

        // 注册事件处理
        _discoveryService.OnDeviceDiscovered += device => OnDeviceDiscovered?.Invoke(device);
        _discoveryService.OnDeviceRemoved += deviceId => OnDeviceRemoved?.Invoke(deviceId);
        _fileTransferService.OnTransferRequestSendAndReceive += info => OnTransferRequestSendAndReceive?.Invoke(info);
        _fileTransferService.OnTransferProgressUpdated += info => OnTransferProgressUpdated?.Invoke(info);
        _fileTransferService.OnTransferCompleted += (info, message) =>
        {
            // 只记录「本机收到的文件」。这里用方向判断而不是 ReceiverId 比较：
            // 手动输入 IP 连接的设备在握手前拿不到真实设备 ID，用 ID 比较会漏记。
            if (info.Status == TransferStatus.Completed && info.Direction == TransferDirection.Receive)
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
        await _discoveryService.StartAnnounceLoopAsync().ConfigureAwait(false);
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
        await _discoveryService.SendServiceQueryAsync().ConfigureAwait(false);
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
    /// 直接按地址连接设备：支持 <c>192.168.1.20</c>、<c>192.168.1.20:5237</c>、<c>[fe80::1]:5237</c>、主机名。
    /// <para>这是跨子网、广播被防火墙拦截、或对端不响应广播时的兜底连接方式：
    /// 先发一次发现报文（可拿到设备名/类型/端口），无应答时退化为 TCP 探活。</para>
    /// </summary>
    /// <param name="address">用户输入的地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测到的设备；不可达时为 <c>null</c>。</returns>
    public async Task<DeviceInfo?> ConnectByAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        var device = await _probeService.ProbeAsync(address, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return null;
        }

        if (device.Port <= 0)
        {
            device.Port = _localDevice.Port;
        }

        // 合并进发现列表，与自动发现的设备享受同样的去重 / 过期 / UI 展示逻辑
        _discoveryService.InjectDevice(device);
        _logger.LogInformation("已通过地址直连设备: {Name} ({Ip}:{Port})", device.DeviceName, device.IpAddress, device.Port);
        return device;
    }

    /// <summary>
    /// 扫描一个网段内所有地址，把发现的设备合并进设备列表。
    /// </summary>
    /// <param name="cidr">网段，如 <c>192.168.1.0/24</c>。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<DeviceInfo>> ScanSubnetAsync(
        string cidr,
        IProgress<SubnetScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var devices = await _subnetScanner.ScanAsync(cidr, progress, cancellationToken).ConfigureAwait(false);

        foreach (var device in devices)
        {
            if (device.Port <= 0)
            {
                device.Port = _localDevice.Port;
            }

            _discoveryService.InjectDevice(device);
        }

        return devices;
    }

    /// <summary>
    /// 按顺序发送多个文件（LocalSend 的「一次发送多个文件」）。
    /// <para>逐个文件复用同一条传输协议，避免一次改动协议帧结构；
    /// 每个文件都会触发 <see cref="OnTransferRequestSendAndReceive"/> 与进度/完成事件，
    /// 因此 UI 里看到的是多条传输记录。</para>
    /// </summary>
    /// <param name="filePaths">文件路径列表。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="progress">批次进度。</param>
    /// <param name="cancellationToken">取消令牌（在文件之间检查）。</param>
    /// <returns>是否全部发送成功。</returns>
    public async Task<bool> SendFilesAsync(
        IReadOnlyList<string> filePaths,
        DeviceInfo targetDevice,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return false;
        }

        var allSucceeded = true;

        for (var index = 0; index < filePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = filePaths[index];
            bool success;
            try
            {
                success = await _fileTransferService.SendFileAsync(filePath, targetDevice, _localDevice.DeviceId)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new FileSendProgress(index + 1, filePaths.Count, filePath, false));
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送文件失败: {Path}", filePath);
                success = false;
            }

            allSucceeded &= success;
            progress?.Report(new FileSendProgress(index + 1, filePaths.Count, filePath, success));
        }

        return allSucceeded;
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
