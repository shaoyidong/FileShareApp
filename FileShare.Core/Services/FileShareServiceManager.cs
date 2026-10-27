using FileShare.Core.Common;
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
    private readonly string? _networkWhitelist;
    private readonly string? _networkBlacklist;
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
    /// 首次信任某设备证书（TOFU）时触发：(设备ID, 指纹)。
    /// </summary>
    public event Action<string, string>? OnFirstUseTrusted;

    /// <summary>
    /// 证书指纹与记录不一致时触发：(设备ID, 已知指纹, 本次指纹)。属于疑似中间人攻击。
    /// </summary>
    public event Action<string, string, string>? OnFingerprintMismatch;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="directoryService">平台目录服务</param>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="deviceName">本地设备名称</param>
    /// <param name="deviceType">设备类型</param>
    /// <param name="transferPort">文件传输端口</param>
    /// <param name="discoveryOptions">自动发现配置</param>
    /// <param name="tlsEnabled">是否启用 TLS 加密传输</param>
    /// <param name="loggerFactory">日志工厂（可选，不传则使用 NullLogger）</param>
    /// <param name="conflictPolicy">接收同名文件时的处理策略</param>
    public FileShareServiceManager(
        IPlatformDirectoryService directoryService,
        IDatabaseService databaseService,
        string deviceName, DeviceType deviceType, int transferPort = 5237,
        DiscoveryOptions? discoveryOptions = null,
        bool tlsEnabled = false,
        ILoggerFactory? loggerFactory = null,
        FileNameConflictPolicy conflictPolicy = FileNameConflictPolicy.Rename,
        int uploadLimitKBps = 0
        )
    {
        _databaseService = databaseService;
        _networkWhitelist = discoveryOptions?.NetworkWhitelist;
        _networkBlacklist = discoveryOptions?.NetworkBlacklist;
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
        _fileTransferService = new TcpFileTransferService(
            deviceId,
            directoryService,
            transferPort,
            tlsEnabled,
            loggerFactory,
            conflictPolicy,
            uploadLimitKBps);

        // 手动直连 / 网段扫描（跨子网与广播被拦截场景的兜底方案）
        _probeService = new DeviceProbeService(_localDevice, transferPort, loggerFactory);
        _subnetScanner = new SubnetScanner(_probeService, loggerFactory);

        // TOFU 事件转发给 UI（首次信任 / 指纹不一致）
        if (_fileTransferService.FingerprintStore is { } fingerprintStore)
        {
            fingerprintStore.FirstUseTrusted += (peerId, fingerprint) => OnFirstUseTrusted?.Invoke(peerId, fingerprint);
            fingerprintStore.FingerprintMismatch += (peerId, known, presented) => OnFingerprintMismatch?.Invoke(peerId, known, presented);
        }

        // 注册事件处理
        _discoveryService.OnDeviceDiscovered += device => OnDeviceDiscovered?.Invoke(device);
        _discoveryService.OnDeviceRemoved += deviceId => OnDeviceRemoved?.Invoke(deviceId);
        _fileTransferService.OnTransferRequestSendAndReceive += info => OnTransferRequestSendAndReceive?.Invoke(info);
        _fileTransferService.OnTransferProgressUpdated += info => OnTransferProgressUpdated?.Invoke(info);
        _fileTransferService.OnTransferCompleted += (info, message) =>
        {
            // 成功 / 失败 / 取消 都记录，方便事后排查「为什么没收到」
            if (info.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
            {
                RecordHistory(info, message);
            }

            OnTransferCompleted?.Invoke(info, message);
        };
    }

    /// <summary>
    /// 记录一条传输历史（接收与发送共用一张表）。
    /// <para>用方向判断而不是 ReceiverId 比较：手动输入 IP 连接的设备在握手前拿不到真实设备 ID，用 ID 比较会漏记。</para>
    /// </summary>
    /// <param name="info">传输信息。</param>
    /// <param name="message">失败/取消原因（成功时为 null）。</param>
    private void RecordHistory(FileTransferInfo info, string? message)
    {
        try
        {
            var isReceive = info.Direction == TransferDirection.Receive;
            var peerId = isReceive ? info.SenderId : info.ReceiverId;
            var peerName = _discoveryService.GetDiscoveredDevices()?
                .FirstOrDefault(device => device.DeviceId == peerId)?.DeviceName;

            // 接收记录用实际落盘的文件名（可能被重命名为 "xxx (1).ext"）
            var fileName = isReceive
                ? info.SavedFileName ?? info.FileName
                : info.FileName;

            // 只有成功的传输才留下可打开的文件：
            // 发送 = 源文件；接收 = 落盘文件（失败/取消时临时文件已删除）
            string? filePath = null;
            if (info.Status == TransferStatus.Completed)
            {
                filePath = info.FilePath;
                if (string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(info.SavePath))
                {
                    filePath = Path.Combine(info.SavePath, fileName);
                }
            }

            var entity = new ReceiveHistoryEntity
            {
                SenderId = peerId,
                SenderDeviceName = peerName,
                PeerId = peerId,
                PeerName = peerName,
                FileName = fileName,
                FileSize = info.FileSize,
                SavePath = info.SavePath ?? string.Empty,
                FilePath = filePath,
                Status = info.Status,
                ErrorMessage = message,
                CreatedAt = DateTime.UtcNow,
                Direction = info.Direction
            };

            _ = _databaseService.AddSingleReceiveHistoryAsync(entity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入传输历史失败: {File}", info.FileName);
        }
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
    /// 发送多个文件（LocalSend 的「一次发送多个文件」）。
    /// <para>每个文件复用同一条传输协议，因此 UI 里看到的是多条传输记录；
    /// <paramref name="parallelism"/> &gt; 1 时会同时开多条连接（默认 1，即逐个发送）。</para>
    /// </summary>
    /// <param name="filePaths">文件路径列表。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="progress">批次进度。</param>
    /// <param name="cancellationToken">取消令牌（在文件之间检查）。</param>
    /// <param name="parallelism">并发数量（1~4）。</param>
    /// <returns>是否全部发送成功。</returns>
    public async Task<bool> SendFilesAsync(
        IReadOnlyList<string> filePaths,
        DeviceInfo targetDevice,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int parallelism = 1,
        TransferQueueControl? queue = null)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return false;
        }

        var items = filePaths
            .Select(path => new FileSendItem(path, null))
            .ToList();

        return await SendItemsAsync(items, targetDevice, progress, cancellationToken, parallelism, queue).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送一个文件夹（递归，保留目录结构）。
    /// </summary>
    /// <param name="folderPath">本地文件夹路径。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="progress">批次进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="parallelism">并发数量（1~4）。</param>
    /// <returns>是否全部发送成功。</returns>
    public async Task<bool> SendFolderAsync(
        string folderPath,
        DeviceInfo targetDevice,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int parallelism = 1,
        TransferQueueControl? queue = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            _logger.LogWarning("文件夹不存在: {Path}", folderPath);
            return false;
        }

        var root = new DirectoryInfo(folderPath);
        var folderName = root.Name;

        var items = new List<FileSendItem>();
        try
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(folderPath, file.FullName);
                // 对端会拼在「保存目录」下，因此带上文件夹自身名字，避免解包后文件散落一地
                items.Add(new FileSendItem(file.FullName, Path.Combine(folderName, relative)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "枚举文件夹失败: {Path}", folderPath);
            return false;
        }

        if (items.Count == 0)
        {
            _logger.LogInformation("文件夹为空，无需发送: {Path}", folderPath);
            return true;
        }

        return await SendItemsAsync(items, targetDevice, progress, cancellationToken, parallelism, queue).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送一段文本：写成本地临时 .txt 后走正常文件传输。
    /// <para>这样无需改动协议即可让对端收到可保存/可打开的文本文件，并且与旧版本客户端兼容。</para>
    /// </summary>
    /// <param name="text">文本内容。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="fileName">可选文件名（默认「文本消息_时间戳.txt」）。</param>
    /// <param name="progress">进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> SendTextAsync(
        string text,
        DeviceInfo targetDevice,
        string? fileName = null,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            return false;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "FileShare", "outgoing");
        Directory.CreateDirectory(tempDirectory);

        var safeName = FileNameHelper.SanitizeFileName(
            fileName ?? $"文本消息_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            "text.txt");

        if (!safeName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            safeName += ".txt";
        }

        var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}_{safeName}");

        try
        {
            await File.WriteAllTextAsync(tempPath, text, cancellationToken).ConfigureAwait(false);

            // 发送时用干净的文件名（临时路径前缀只用于本地去重）
            var stagingPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.send");
            File.Move(tempPath, stagingPath, overwrite: true);

            var success = await _fileTransferService
                .SendFileAsync(stagingPath, targetDevice, _localDevice.DeviceId, null, safeName)
                .ConfigureAwait(false);

            progress?.Report(new FileSendProgress(1, 1, safeName, success));
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送文本失败");
            return false;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// 逐个/并发发送一组文件。
    /// </summary>
    private async Task<bool> SendItemsAsync(
        IReadOnlyList<FileSendItem> items,
        DeviceInfo targetDevice,
        IProgress<FileSendProgress>? progress,
        CancellationToken cancellationToken,
        int parallelism,
        TransferQueueControl? queue)
    {
        var concurrency = Math.Clamp(parallelism, 1, 4);
        var completed = 0;
        var allSucceeded = true;
        var gate = concurrency > 1 ? new SemaphoreSlim(concurrency, concurrency) : null;

        // 队列控制（暂停/取消）与外部取消令牌合并
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            queue?.Token ?? CancellationToken.None);
        var effectiveToken = linkedCts.Token;

        try
        {
            var tasks = items.Select(async item =>
            {
                if (gate is not null)
                {
                    await gate.WaitAsync(effectiveToken).ConfigureAwait(false);
                }

                // 暂停只发生在文件之间：当前正在传的文件不会被中断
                if (queue is not null)
                {
                    await queue.WaitWhilePausedAsync(effectiveToken).ConfigureAwait(false);
                }

                bool success;
                try
                {
                    effectiveToken.ThrowIfCancellationRequested();

                    success = await _fileTransferService
                        .SendFileAsync(item.FilePath, targetDevice, _localDevice.DeviceId, item.RelativePath, null)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "发送文件失败: {Path}", item.FilePath);
                    success = false;
                }
                finally
                {
                    gate?.Release();
                }

                if (!success)
                {
                    allSucceeded = false;
                }

                var index = Interlocked.Increment(ref completed);
                progress?.Report(new FileSendProgress(index, items.Count, item.RelativePath ?? item.FilePath, success));
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return allSucceeded;
        }
        finally
        {
            gate?.Dispose();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 临时文件清理失败可忽略
        }
    }

    /// <summary>发送队列中的一项：本地路径 + 对端相对路径。</summary>
    private sealed record FileSendItem(string FilePath, string? RelativePath);

    /// <summary>
    /// 发送单个文件（队列逐项发送时使用）：遵循暂停 / 取消契约。
    /// </summary>
    /// <param name="filePath">本地文件路径。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="relativePath">文件夹相对路径（可选）。</param>
    /// <param name="queue">队列控制（可为空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> SendSingleAsync(
        string filePath,
        DeviceInfo targetDevice,
        string? relativePath = null,
        TransferQueueControl? queue = null,
        CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            queue?.Token ?? CancellationToken.None);

        if (queue is not null)
        {
            await queue.WaitWhilePausedAsync(linkedCts.Token).ConfigureAwait(false);
        }

        linkedCts.Token.ThrowIfCancellationRequested();

        return await _fileTransferService
            .SendFileAsync(filePath, targetDevice, _localDevice.DeviceId, relativePath, null)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 读取当前已信任的设备指纹。
    /// </summary>
    public IReadOnlyList<(string DeviceId, string Fingerprint, DateTime NotAfterUtc)> GetTrustedDevices()
    {
        return _fileTransferService.FingerprintStore?.GetTrustedDevices()
               ?? Array.Empty<(string, string, DateTime)>();
    }

    /// <summary>
    /// 移除某个设备的信任记录。
    /// </summary>
    public bool ForgetTrustedDevice(string deviceId)
    {
        return _fileTransferService.FingerprintStore?.Forget(deviceId) ?? false;
    }

    /// <summary>
    /// 清空所有信任记录。
    /// </summary>
    public void ClearTrustedDevices()
    {
        _fileTransferService.FingerprintStore?.Clear();
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
