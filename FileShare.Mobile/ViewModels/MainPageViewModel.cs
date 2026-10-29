using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Mobile.Messages;
using FileShare.Mobile.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;

namespace FileShare.Mobile.ViewModels;

public partial class MainPageViewModel : ViewModelBase
{
    private readonly IFileShareServiceManager _serviceManager;
    private readonly SynchronizationContext _uiContext;
    private readonly IAlertService _alertService;
    private readonly string _localDeviceId;
    private readonly IFileTransferForegroundService _foregroundService;
    private readonly IPickerService _filePickerService;
    private readonly IAppSettingsService _settingsService;
    private readonly IAppManagementService _appManagementService;
    private readonly ILogger<MainPageViewModel> _logger;

    private string _statusMessage = "准备就绪";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
   

    /// <summary>手动直连地址（ip / ip:port / 主机名）。</summary>
    private string _manualAddress = string.Empty;
    public string ManualAddress
    {
        get => _manualAddress;
        set => SetProperty(ref _manualAddress, value);
    }

    /// <summary>网段扫描输入（CIDR）。</summary>
    private string _subnetCidr = "192.168.1.0/24";
    public string SubnetCidr
    {
        get => _subnetCidr;
        set => SetProperty(ref _subnetCidr, value);
    }

    private bool _isSubnetScanning;
    public bool IsSubnetScanning
    {
        get => _isSubnetScanning;
        set
        {
            if (SetProperty(ref _isSubnetScanning, value))
            {
                OnPropertyChanged(nameof(IsNotSubnetScanning));
            }
        }
    }

    /// <summary>未在扫描（用于按钮可用状态）。</summary>
    public bool IsNotSubnetScanning => !IsSubnetScanning;

    private double _subnetScanProgress01;
    /// <summary>扫描进度（0~1，供 ProgressBar 绑定）。</summary>
    public double SubnetScanProgress01
    {
        get => _subnetScanProgress01;
        set => SetProperty(ref _subnetScanProgress01, value);
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set
            {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsNotScanning));
            }
        }           
    }

    public bool IsNotScanning => !IsScanning;

    private Core.Models.DeviceInfo? _selectedDevice;
    public Core.Models.DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(IsDeviceSelected));
            }
        }
    }

    public bool IsDeviceSelected => SelectedDevice != null;

    /// <summary>是否展开设置面板。</summary>
    [ObservableProperty]
    private bool _isSettingsVisible;

    /// <summary>设备名。</summary>
    [ObservableProperty]
    private string _deviceNameText = string.Empty;

    /// <summary>是否启用加密传输。</summary>
    [ObservableProperty]
    private bool _tlsEnabledSetting = true;

    /// <summary>保存位置文本。</summary>
    [ObservableProperty]
    private string _saveDirectoryText = "按文件类型自动归类";

    /// <summary>设置面板提示。</summary>
    [ObservableProperty]
    private string _settingsStatus = string.Empty;

    public ICommand RefreshDevicesCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand SendMultipleFilesCommand { get; }
    public ICommand SendFolderCommand { get; }
    public ICommand ChooseSaveDirectoryCommand { get; }
    public ICommand SendTextCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CancelTransferCommand { get; }
    public ICommand ClearTrustedDevicesCommand { get; }
    public ICommand NavigateToAppListCommand { get; }
    public ICommand ConnectByAddressCommand { get; }
    public ICommand ScanSubnetCommand { get; }

    public ObservableCollection<FileShare.Core.Models.DeviceInfo> Devices { get; }
    public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
    public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
    public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }
    
    public MainPageViewModel(IPlatformDirectoryService directoryService
        , IFileShareServiceManager fileShareServiceManager
        , IFileTransferForegroundService fileTransferService
        , IAlertService alertService
        , IPickerService filePickerService
        , IAppSettingsService settingsService
        , IAppManagementService appManagementService
        , ILoggerFactory loggerFactory)
    {
        _uiContext = SynchronizationContext.Current?? new SynchronizationContext();
        _serviceManager = fileShareServiceManager;
        _foregroundService = fileTransferService;
        _alertService = alertService;
        _filePickerService = filePickerService;
        _settingsService = settingsService;
        _appManagementService = appManagementService;
        _logger = loggerFactory.CreateLogger<MainPageViewModel>();
        Devices = new ObservableCollection<FileShare.Core.Models.DeviceInfo>();
        TransferTasks = new ObservableCollection<FileTransferViewModel>();
        SentTransferTasks = new ObservableCollection<FileTransferViewModel>();
        ReceivedTransferTasks = new ObservableCollection<FileTransferViewModel>();

        LoadSettings();

        // 保存本地设备ID
        _localDeviceId = _serviceManager.GetLocalDeviceInfo().DeviceId;
        
        // 注册事件处理
        _serviceManager.OnDeviceDiscovered += OnDeviceDiscovered;
        _serviceManager.OnDeviceRemoved += OnDeviceRemoved;
        _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestSendAndReceive;
        _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
        _serviceManager.OnTransferCompleted += OnTransferCompleted;

        // TLS/TOFU：首次信任静默记录；证书指纹变化必须提示用户（否则连接会莫名其妙失败）
        _serviceManager.OnFirstUseTrusted += OnFirstUseTrusted;
        _serviceManager.OnFingerprintMismatch += OnFingerprintMismatch;

        // 订阅WeakReferenceMessenger消息，接收从AppListPage返回的APK路径
        WeakReferenceMessenger.Default.Register<AppSelectedMessage>(this, async (recipient, message) =>
        {
            await HandleAppSelected(message.Value);
        });

        // 初始化命令
        RefreshDevicesCommand = new RelayCommand(async () => RefreshDevicesAsync());
        SendFileCommand = new RelayCommand(async () => SendFileAsync());
        SendMultipleFilesCommand = new RelayCommand(() => _ = SendMultipleFilesAsync());
        SendFolderCommand = new RelayCommand(() => _ = SendFolderAsync());
        ChooseSaveDirectoryCommand = new RelayCommand(() => _ = ChooseSaveDirectoryAsync());
        SendTextCommand = new RelayCommand(() => _ = SendTextAsync());
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsVisible = !IsSettingsVisible);
        SaveSettingsCommand = new RelayCommand(() => _ = SaveSettingsAsync());
        CancelTransferCommand = new RelayCommand<FileTransferViewModel>(CancelTransfer);
        ClearTrustedDevicesCommand = new RelayCommand(() => _ = ClearTrustedDevicesAsync());
        NavigateToAppListCommand = new RelayCommand(async () => await NavigateToAppList());
        ConnectByAddressCommand = new RelayCommand(async () => await ConnectByAddressAsync());
        ScanSubnetCommand = new RelayCommand(async () => await ScanSubnetAsync());
        
        // 启动服务
        _ = InitializeAsync();
        //InitializeFileTransferService();
#if DEBUG
        Devices.Add(new Core.Models.DeviceInfo
        {
            DeviceId = "HuaWei Mate 60",
            DeviceName = "HuaWei Mate 60",
            DeviceType = Core.Models.DeviceType.Mobile,
            IpAddress = "ipaddrss",
            LastSeen = DateTime.Now
        });
        Devices.Add(new Core.Models.DeviceInfo
        {
            DeviceId = "123",
            DeviceName = "Mac Pad",
            DeviceType = Core.Models.DeviceType.Tablet,
            IpAddress = "127.0.0.1",
            LastSeen = DateTime.Now
        });
        Devices.Add(new Core.Models.DeviceInfo
        {
            DeviceId = "1234",
            DeviceName = "jiaolong 15Pro",
            DeviceType = Core.Models.DeviceType.Desktop,
            IpAddress = "127.0.0.3",
            LastSeen = DateTime.Now
        });

        FileTransferViewModel fileTransferViewModel = new FileTransferViewModel
        {
            TransferId = Guid.NewGuid().ToString(),
            FileName = "示例文件.txt",
            FileSize = 1024 * 1024 * 5,
            TransferredSize = 1024 * 1024 * 2,
            ProgressPercentage = 40,
            Status = TransferStatus.Pending,
            SenderId = "123",
            ReceiverId = _localDeviceId,
        };
        FileTransferViewModel fileTransferViewModel2 = new FileTransferViewModel
        {
            TransferId = Guid.NewGuid().ToString(),
            FileName = "示例文件2.txt",
            FileSize = 1024 * 1024 * 5,
            TransferredSize = 1024 * 1024 * 2,
            ProgressPercentage = 40,
            Status = TransferStatus.Transferring,
            SenderId = "1233",
            ReceiverId = _localDeviceId,
        };
        TransferTasks.Add(fileTransferViewModel);
        TransferTasks.Add(fileTransferViewModel2);
        ReceivedTransferTasks.Add(fileTransferViewModel);
        ReceivedTransferTasks.Add(fileTransferViewModel2);

        FileTransferViewModel fileTransferViewModel3 = new FileTransferViewModel
        {
            TransferId = Guid.NewGuid().ToString(),
            FileName = "示例文件3.txt",
            FileSize = 1024 * 1024 * 5,
            TransferredSize = 1024 * 1024 * 2,
            ProgressPercentage = 40,
            Status = TransferStatus.Pending,
            Direction = TransferDirection.Receive,
            SenderId = _localDeviceId,
            ReceiverId = "1234",
        };
        FileTransferViewModel fileTransferViewModel4 = new FileTransferViewModel
        {
            TransferId = Guid.NewGuid().ToString(),
            FileName = "示例文件4.txt",
            FileSize = 1024 * 1024 * 5,
            TransferredSize = 1024 * 1024 * 2,
            ProgressPercentage = 40,
            Status = TransferStatus.Transferring,
            Direction = TransferDirection.Receive,
            SenderId = _localDeviceId,
            ReceiverId = "1234",
        };

        TransferTasks.Add(fileTransferViewModel3);
        TransferTasks.Add(fileTransferViewModel4);
        SentTransferTasks.Add(fileTransferViewModel3);
        SentTransferTasks.Add(fileTransferViewModel4);
#endif
    }

    private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        _uiContext.Post((o) =>
        {
            List<Core.Models.DeviceInfo> removeDevices = new List<Core.Models.DeviceInfo>();
            for (int i = 0; i < Devices.Count; i++)
            {
                if ((DateTime.Now - Devices[i].LastSeen) > TimeSpan.FromSeconds(10))
                {
                    removeDevices.Add(Devices[i]);
                }
            }
            foreach (var item in removeDevices)
            {
                Devices.Remove(item);
            }
            removeDevices.Clear();
        },null);
    }
    
    /// <summary>
    /// 手动输入 IP 直连（跨子网 / 广播被拦截时的兜底方案）。
    /// </summary>
    private async Task ConnectByAddressAsync()
    {
        var input = ManualAddress?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            StatusMessage = "请输入设备 IP，例如 192.168.1.20 或 192.168.1.20:5237";
            return;
        }

        if (!FileShare.Core.Network.Discovery.DeviceAddress.TryParse(input, 5237, out _, out var error))
        {
            StatusMessage = error ?? "地址格式不正确";
            return;
        }

        StatusMessage = $"正在连接 {input} ...";
        try
        {
            var device = await _serviceManager.ConnectByAddressAsync(input);
            if (device is null)
            {
                StatusMessage = $"无法连接 {input}，请确认对端已启动";
                return;
            }

            StatusMessage = $"已连接 {device.DeviceName} ({device.IpAddress}:{device.Port})";
            ManualAddress = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接失败: {ex.Message}";
            _logger.LogError(ex, "手动连接失败");
        }
    }

    /// <summary>一次选择多个文件发送（顺序发送）。</summary>
    private async Task SendMultipleFilesAsync()
    {
        if (SelectedDevice == null)
        {
            await _alertService.DisplayToastAsync("请先选择目标设备").ConfigureAwait(false);
            return;
        }

        try
        {
            var files = await _filePickerService.PickFilesAsync(new PickOptions
            {
                PickerTitle = "选择要发送的文件（可多选）"
            }).ConfigureAwait(false);

            if (files.Count == 0)
            {
                return;
            }

            await _foregroundService.StartServiceAsync().ConfigureAwait(false);

            _uiContext.Post(_ => StatusMessage = $"正在发送 {files.Count} 个文件...", null);

            var paths = files.Select(file => file.FullPath).Where(path => !string.IsNullOrEmpty(path)).ToList();
            await _serviceManager.SendFilesAsync(paths, SelectedDevice).ConfigureAwait(false);

            _uiContext.Post(_ => StatusMessage = StatusMessage, null);
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文件失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
            _logger.LogError(ex, "批量发送文件失败");
        }
        finally
        {
            StopForegroundServiceIfNoTasksRunning();
        }
    }

    /// <summary>选择文件夹并递归发送（保留目录结构）。</summary>
    private async Task SendFolderAsync()
    {
        if (SelectedDevice == null)
        {
            await _alertService.DisplayToastAsync("请先选择目标设备").ConfigureAwait(false);
            return;
        }

        try
        {
            var folder = await _filePickerService.PickFolderAsync("选择要发送的文件夹").ConfigureAwait(false);

            if (string.IsNullOrEmpty(folder))
            {
                await _alertService.DisplayToastAsync("当前平台不支持直接选择文件夹，可用「发送多个文件」").ConfigureAwait(false);
                return;
            }

            await _foregroundService.StartServiceAsync().ConfigureAwait(false);

            _uiContext.Post(_ => StatusMessage = $"正在发送文件夹: {Path.GetFileName(folder)}", null);

            await _serviceManager.SendFolderAsync(folder, SelectedDevice).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文件夹失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
            _logger.LogError(ex, "发送文件夹失败");
        }
        finally
        {
            StopForegroundServiceIfNoTasksRunning();
        }
    }

    /// <summary>
    /// 选择接收文件的保存位置（留空则按文件类型自动归类到下载/图片等目录）。
    /// </summary>
    private async Task ChooseSaveDirectoryAsync()
    {
        try
        {
            var folder = await _filePickerService.PickFolderAsync("选择接收文件的保存位置", forSaving: true).ConfigureAwait(false);

            if (string.IsNullOrEmpty(folder))
            {
                await _alertService.DisplayToastAsync("当前平台不支持直接选择文件夹").ConfigureAwait(false);
                return;
            }

            _settingsService.Update(settings => settings.SaveDirectory = folder);
            _uiContext.Post(_ => StatusMessage = $"保存位置：{folder}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择保存目录失败");
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 扫描一个网段，把发现的设备加入列表。
    /// </summary>
    private async Task ScanSubnetAsync()
    {
        if (IsSubnetScanning)
        {
            return;
        }

        if (!FileShare.Core.Network.Discovery.SubnetScanner.TryParseCidr(SubnetCidr, out _, out _, out var error))
        {
            StatusMessage = error ?? "网段格式不正确";
            return;
        }

        IsSubnetScanning = true;
        SubnetScanProgress01 = 0;

        var progress = new Progress<FileShare.Core.Network.Discovery.SubnetScanProgress>(item =>
        {
            SubnetScanProgress01 = item.Percent / 100d;
            if (item.CurrentAddress is not null)
            {
                StatusMessage = $"正在扫描 {item.CurrentAddress} ... ({item.Scanned}/{item.Total})";
            }
        });

        try
        {
            var devices = await _serviceManager.ScanSubnetAsync(SubnetCidr, progress);
            StatusMessage = $"扫描完成，网段 {SubnetCidr} 内发现 {devices.Count} 台设备";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败: {ex.Message}";
            _logger.LogError(ex, "网段扫描失败");
        }
        finally
        {
            IsSubnetScanning = false;
            SubnetScanProgress01 = 0;
        }
    }
    private async Task InitializeAsync()
    {
        try
        {
            await _serviceManager.StartServicesAsync();
            StatusMessage = "准备就绪";
            _logger.LogInformation("服务启动成功");
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动服务失败: {ex.Message}";
            _logger.LogError(ex, "启动服务失败");
        }
    }
    
//    private void InitializeFileTransferService()
//    {
//        // 如果服务实例不存在，创建一个默认实例
//        if (_fileTransferService == null)
//        {

//#if ANDROID
//            _fileTransferService = new AndroidFileTransferForegroundService();
//#else
//            // 其他平台使用默认实现
//             _fileTransferService = new DefaultFileTransferForegroundService();
//#endif
//        }

//        // 初始化服务，传入服务管理器
//        _fileTransferService.Initialize(_serviceManager);
//    }    
   
    private async Task RefreshDevicesAsync()
    {
        try
        {
            IsScanning = true;
            StatusMessage = "正在扫描设备...";
            await _serviceManager.RefreshDevicesAsync();
            await Task.Delay(2000);
            StatusMessage = "扫描完成";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描设备失败: {ex.Message}";
            _logger.LogError(ex, "扫描设备失败");
        }
        finally
        {
            IsScanning = false;
        }
    }
    
    private async Task SendFileAsync()
    {
        if (SelectedDevice == null)
        {
            await _alertService.DisplayToastAsync("请先选择目标设备").ConfigureAwait(false);
            return;
        }
        
        try
        {
            await _foregroundService.StartServiceAsync();
            // 打开文件选择器
            var result = await _filePickerService.PickFileAsync(new PickOptions
            {
                PickerTitle = "选择要发送的文件",
                
            }).ConfigureAwait(false);
         
            if (result != null)
            {
                _uiContext.Post((o) =>
                {
                    StatusMessage = $"正在发送文件: {result.FileName}";
                }, null);

                await _serviceManager.SendFileAsync(result.FullPath, SelectedDevice).ConfigureAwait(false);
                _logger.LogInformation("文件发送成功: {FilePath} 到设备 {DeviceName}", result.FullPath, SelectedDevice.DeviceName);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文件失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
            _logger.LogError(ex, "发送文件失败到设备 {DeviceName}", SelectedDevice?.DeviceName);
        }
        finally
        {
            StopForegroundServiceIfNoTasksRunning();
        }
    }

    /// <summary>把当前设置载入界面（构造时调用）。</summary>
    private void LoadSettings()
    {
        var settings = _settingsService.Current;
        DeviceNameText = settings.DeviceName;
        TlsEnabledSetting = settings.TlsEnabled;
        SaveDirectoryText = string.IsNullOrWhiteSpace(settings.SaveDirectory)
            ? "按文件类型自动归类"
            : settings.SaveDirectory!;
    }

    private async Task SaveSettingsAsync()
    {
        var name = DeviceNameText?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SettingsStatus = "设备名称不能为空";
            return;
        }

        _settingsService.Update(settings =>
        {
            settings.DeviceName = name;
            settings.TlsEnabled = TlsEnabledSetting;
            settings.SaveDirectory = SaveDirectoryText is null or "按文件类型自动归类"
                ? null
                : SaveDirectoryText!.Trim();
        });

        SettingsStatus = "已保存；设备名称 / 加密 / 端口需重启应用生效";
        await _alertService.DisplayToastAsync("设置已保存（部分需重启生效）").ConfigureAwait(false);
    }

    /// <summary>发送文本（对端收到一个 .txt 文件）。</summary>
    private async Task SendTextAsync()
    {
        if (SelectedDevice == null)
        {
            await _alertService.DisplayToastAsync("请先选择目标设备").ConfigureAwait(false);
            return;
        }

        var text = await _alertService.DisplayPromptAsync("发送文本", "输入要发送的文本，对端会收到一个 .txt 文件", "发送", "取消").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await _foregroundService.StartServiceAsync().ConfigureAwait(false);
            _uiContext.Post(_ => StatusMessage = $"正在发送文本（{text.Length} 字符）...", null);

            var success = await _serviceManager.SendTextAsync(text, SelectedDevice).ConfigureAwait(false);
            _uiContext.Post(_ => StatusMessage = success ? "文本已发送" : "文本发送失败", null);
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文本失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
            _logger.LogError(ex, "发送文本失败");
        }
        finally
        {
            StopForegroundServiceIfNoTasksRunning();
        }
    }

    /// <summary>取消一笔传输。</summary>
    private void CancelTransfer(FileTransferViewModel? viewModel)
    {
        if (viewModel?.TransferId is null)
        {
            return;
        }

        try
        {
            _serviceManager.CancelTransfer(viewModel.TransferId);
            viewModel.Status = TransferStatus.Cancelled;
            StatusMessage = $"已取消: {viewModel.FileName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消传输失败");
        }
    }
    private void OnFirstUseTrusted(string peerId, string fingerprint)
    {
        _logger.LogInformation("已记录新设备的证书指纹: {Peer} {Fingerprint}", peerId, fingerprint);
    }

    private void OnFingerprintMismatch(string peerId, string knownFingerprint, string presentedFingerprint)
    {
        _logger.LogWarning(
            "证书指纹不一致（疑似中间人），已拒绝: {Peer} 已知={Known} 本次={Presented}",
            peerId, knownFingerprint, presentedFingerprint);

        _ = _alertService.DisplayAlertAsync(
            "安全警告",
            $"设备 {peerId} 的证书指纹与之前记录的不一致，连接已被拒绝。\n\n如果确认对方重装过系统，可在「设置 → 清除信任记录」后重试。",
            "知道了");
    }

    /// <summary>清除全部 TOFU 信任记录（设置里用）。</summary>
    private async Task ClearTrustedDevicesAsync()
    {
        var confirmed = await _alertService.DisplayAlertAsync(
            "清除信任记录",
            "清除后，所有设备下次连接都会重新记录证书指纹。确定继续吗？",
            "清除",
            "取消").ConfigureAwait(false);

        if (!confirmed)
        {
            return;
        }

        _serviceManager.ClearTrustedDevices();
        SettingsStatus = "已清除全部信任记录";
        await _alertService.DisplayToastAsync("已清除全部信任记录").ConfigureAwait(false);
    }
    private void StopForegroundServiceIfNoTasksRunning()
    {
        if (!TransferTasks.Any(t => t.Status == TransferStatus.Pending || t.Status == TransferStatus.Transferring))
        {
            _foregroundService.StopService();
        }
    }

    [RelayCommand]
    private async Task AcceptTransfer(FileTransferViewModel viewModel)
    {
        try
        {
            if (viewModel?.TransferId == null)
            {
                return;
            }

            _serviceManager.HandleTransferRequest(viewModel.TransferId, true, _settingsService.Current.SaveDirectory);
            StatusMessage = $"开始接收文件: {viewModel.FileName}";
            _logger.LogInformation("开始接收文件: {FileName}", viewModel.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"接受传输失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
            _logger.LogError(ex, "接受传输失败: {FileName}", viewModel?.FileName);
        }
    }

    [RelayCommand]
    private async Task RejectTransfer(FileTransferViewModel viewModel)
    {
        if (viewModel?.TransferId == null)
        {
            return;
        }

        _serviceManager.HandleTransferRequest(viewModel.TransferId, false);
        await _alertService.DisplayToastAsync("拒绝").ConfigureAwait(false);
        StatusMessage = $"已拒绝文件: {viewModel.FileName}";
        _logger.LogInformation("已拒绝文件: {FileName}", viewModel.FileName);
    }
    
    [RelayCommand]
    private async Task RemoveTransfer(FileTransferViewModel viewModel)
    {
        if (viewModel?.TransferId == null)
        {
            return;
        }

        switch (viewModel.Status)
        {
            case TransferStatus.Pending:
                _serviceManager.CancelTransfer(viewModel.TransferId);
                StatusMessage = $"已拒绝文件: {viewModel.FileName}";
                _logger.LogInformation("已拒绝文件: {FileName}", viewModel.FileName);
                break;
            case TransferStatus.Transferring:
                // 显示确认对话框
                var result = await _alertService.DisplayAlertAsync(
                    "确认移除",
                    "正在传输中，确定要移除吗？",
                    "确定", "取消");

                if (!result)
                {
                    return;
                }

                _serviceManager.CancelTransfer(viewModel.TransferId);
                StatusMessage = $"已取消传输: {viewModel.FileName}";
                _logger.LogInformation("已取消文件传输: {FileName}", viewModel.FileName);
                break;
            case TransferStatus.Completed:
            case TransferStatus.Failed:
            case TransferStatus.Cancelled:
                break;
            default:
                break;
        }
        
        // 从列表中移除
        TransferTasks.Remove(viewModel);
        if (viewModel.SenderId == _localDeviceId)
        {
            SentTransferTasks.Remove(viewModel);
        }
        else
        {
            ReceivedTransferTasks.Remove(viewModel);
        }
    }
    
    private void OnDeviceDiscovered(FileShare.Core.Models.DeviceInfo device)
    {
        // 更新设备列表，过滤掉本地设备
        var localDevice = _serviceManager.GetLocalDeviceInfo();
        if (device.DeviceId == localDevice.DeviceId)
        {
            return;
        }

        _uiContext.Post((o) =>
        {
            // 检查设备是否已存在
            var existingDevice = Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            if (existingDevice == null)
            {
                Devices.Add(device);
                StatusMessage = $"发现设备: {device.DeviceName}";
                _logger.LogInformation("发现设备: {DeviceName} (ID: {DeviceId})", device.DeviceName, device.DeviceId);
            }
        },null);
    }

    private void OnDeviceRemoved(string deviceId)
    {
        // 更新设备列表，过滤掉本地设备
        var localDevice = _serviceManager.GetLocalDeviceInfo();
        if (deviceId == localDevice.DeviceId)
        {
            return;
        }

        _uiContext.Post((o) =>
        {
            // 检查设备是否存在
            var existingDevice = Devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (existingDevice != null)
            {
                Devices.Remove(existingDevice);
                StatusMessage = $"设备已离线: {existingDevice.DeviceName}";
                _logger.LogInformation("设备已离线: {DeviceName} (ID: {DeviceId})", existingDevice.DeviceName, existingDevice.DeviceId);
            }
        },null);
    }
    
    private async void OnTransferRequestSendAndReceive(FileTransferInfo info)
    {
        await _foregroundService.StartServiceAsync().ConfigureAwait(false);
        _uiContext.Post(async (o) =>
        {
            // 创建FileTransferViewModel实例
            var transferViewModel = FileTransferViewModel.FromModel(info);
            
            // 添加到总任务列表
            TransferTasks.Insert(0, transferViewModel);
            
            // 根据发送者ID判断是发送任务还是接收任务
            if (info.SenderId == _localDeviceId)
            {
                // 发送任务
                SentTransferTasks.Insert(0, transferViewModel);
                StatusMessage = $"正在发送文件: {info.FileName}";
                _logger.LogInformation("准备发送文件: {FileName} (ID: {TransferId})", info.FileName, info.TransferId);
            }
            else
            {
                // 接收任务
                ReceivedTransferTasks.Insert(0, transferViewModel);
                StatusMessage = $"收到文件传输请求: {info.FileName}";
                _logger.LogInformation("收到文件传输请求: {FileName} (ID: {TransferId})", info.FileName, info.TransferId);
                
                var senderDevice = Devices.FirstOrDefault(d => d.DeviceId == info.SenderId);
                if (senderDevice == null) 
                {
                    return;
                }
                //transferViewModel.DeviceName = senderDevice.DeviceName;
                // 显示确认对话框
                var result = await _alertService.DisplayAlertAsync(
                    "文件传输请求",
                    $"来自 {senderDevice.DeviceName ?? "未知设备"} 的文件: {info.FileName} ({transferViewModel.FormattedFileSize})",
                    "接受", "拒绝");
                
                if (result)
                {
                    await AcceptTransfer(transferViewModel);
                }
                else
                {
                    RejectTransferCommand.Execute(transferViewModel);
                }
            }
        },null);
    }
    
    private void OnTransferProgressUpdated(FileTransferInfo updatedInfo)
    {
        _uiContext.Post((o) =>
        {
            // 查找对应的FileTransferViewModel并更新
            var viewModel = TransferTasks.FirstOrDefault(t => t.TransferId == updatedInfo.TransferId);
            if (viewModel != null)
            {
                viewModel.Status = updatedInfo.Status;
                viewModel.FileSize = updatedInfo.FileSize;
                viewModel.TransferredSize = updatedInfo.TransferredSize;
                viewModel.ProgressPercentage = updatedInfo.ProgressPercentage;

                StatusMessage = $"正在传输: {updatedInfo.FileName} ({updatedInfo.ProgressPercentage:F1}%)";
            }
        },null);
    }
    
    private void OnTransferCompleted(FileTransferInfo updatedInfo, string? errorMessage)
    {
        _uiContext.Post((o) =>
        {
            // 查找对应的FileTransferViewModel并更新
            var viewModel = TransferTasks.FirstOrDefault(t => t.TransferId == updatedInfo.TransferId);
            if (viewModel != null)
            {
                viewModel.Status = updatedInfo.Status;
                viewModel.FileSize = updatedInfo.FileSize;
                viewModel.TransferredSize = updatedInfo.TransferredSize;
                viewModel.ProgressPercentage = updatedInfo.ProgressPercentage;

                switch (viewModel.Status)
                {
                    case TransferStatus.Completed:
                        StatusMessage = $"文件传输完成: {updatedInfo.FileName}";
                        _logger.LogInformation("文件传输完成: {FileName} (ID: {TransferId})", updatedInfo.FileName, updatedInfo.TransferId);
                        break;
                    case TransferStatus.Failed:
                        StatusMessage = $"文件传输失败: {errorMessage??string.Empty}";
                        _logger.LogError("文件传输失败: {FileName} (ID: {TransferId}), 错误信息: {ErrorMessage}", updatedInfo.FileName, updatedInfo.TransferId, errorMessage);
                        break;
                    case TransferStatus.Cancelled:
                        StatusMessage = $"文件传输取消: {errorMessage ?? string.Empty}";
                        _logger.LogInformation("文件传输取消: {FileName} (ID: {TransferId}), 错误信息: {ErrorMessage}", updatedInfo.FileName, updatedInfo.TransferId, errorMessage);
                        break;
                    default:
                        StatusMessage = errorMessage ?? string.Empty;
                        break;
                }
            }
        },null);
        StopForegroundServiceIfNoTasksRunning();
    } 
    
    public void Dispose()
    {
        _serviceManager.Dispose();
        
        // 停止文件传输服务
        _foregroundService?.StopService();
    }
    
    private async Task HandleAppSelected(string apkPath)
    {
        if (SelectedDevice == null)
        {
            await _alertService.DisplayToastAsync("请先选择目标设备").ConfigureAwait(false);
            return;
        }
        
        try
        {
            if (!string.IsNullOrEmpty(apkPath))
            {
                // 启动前台服务
                await _foregroundService.StartServiceAsync().ConfigureAwait(false);
                
                // 更新状态消息
                _uiContext.Post((o) =>
                {
                    StatusMessage = $"正在发送应用: {Path.GetFileName(apkPath)}";
                }, null);
                
                // 执行发送流程
                await _serviceManager.SendFileAsync(apkPath, SelectedDevice).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync("错误, 发送应用失败: " + ex.Message);
            _logger.LogError(ex, "发送应用失败: {ApkPath}", apkPath);
        }
        finally
        {
            StopForegroundServiceIfNoTasksRunning();
        }
    }

    private async Task NavigateToAppList()
    {
        if (SelectedDevice == null)
        {
            await _alertService.DisplayToastAsync("请先选择目标设备").ConfigureAwait(false);
            return;
        }

        try
        {
            // 导航到AppListPage
            await Shell.Current.GoToAsync("/AppListPage");
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync("错误, 导航到应用列表页面失败: " + ex.Message);
            _logger.LogError(ex, "导航到应用列表页面失败");
        }
    }
}
