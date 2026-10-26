using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Network.Discovery;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileShare.Desktop.ViewModels
{
    /// <summary>
    /// 主界面视图模型：设备发现、手动 IP 直连、网段扫描、文件发送与传输任务管理。
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        private readonly IFileShareServiceManager _serviceManager;
        private readonly IDialogService _dialogService;
        private readonly IAppSettingsService _settingsService;
        private readonly IThemeService _themeService;
        private readonly SynchronizationContext _uiContext;
        private readonly IClassicDesktopStyleApplicationLifetime? _appLifetime;
        private readonly ILogger<MainViewModel> _logger;
        private readonly string _localDeviceId;

        private CancellationTokenSource? _scanCts;

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        private bool _isSubnetScanning;

        [ObservableProperty]
        private bool _isConnecting;

        [ObservableProperty]
        private double _subnetScanProgress;

        [ObservableProperty]
        private string _subnetCidr = "192.168.1.0/24";

        [ObservableProperty]
        private string _manualAddress = string.Empty;

        [ObservableProperty]
        private string _statusMessage = "准备就绪";

        [ObservableProperty]
        private object? _currentView;

        [ObservableProperty]
        private DeviceItemViewModel? _selectedItem;

        [ObservableProperty]
        private string _localDeviceSummary = string.Empty;

        /// <summary>本机设备信息。</summary>
        public DeviceInfo? LocalDevice { get; }

        /// <summary>窗口标题。</summary>
        public string WindowTitle => LocalDevice is null
            ? "FileShare 局域网传输"
            : $"FileShare 局域网传输 - {LocalDevice.DeviceName} ({LocalDevice.IpAddress})";

        /// <summary>
        /// 当前选中的设备。保留 <see cref="DeviceInfo"/> 形态，便于外部/测试直接赋值。
        /// </summary>
        public DeviceInfo? SelectedDevice
        {
            get => SelectedItem?.Device;
            set
            {
                if (value is null)
                {
                    SelectedItem = null;
                    return;
                }

                var item = Devices.FirstOrDefault(device => device.DeviceId == value.DeviceId);
                if (item is null)
                {
                    item = new DeviceItemViewModel(value, _settingsService.Current.IsFavorite(value.DeviceId));
                    Devices.Add(item);
                }

                SelectedItem = item;
            }
        }

        public bool IsDeviceSelected => SelectedDevice is not null;

        public bool HasTransferTasks => TransferTasks.Count > 0;

        /// <summary>设备数量文本。</summary>
        public string DeviceCountText => $"共 {Devices.Count} 台";

        public ObservableCollection<DeviceItemViewModel> Devices { get; }
        public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand SendFilesCommand { get; }
        public ICommand ConnectByAddressCommand { get; }
        public ICommand ScanSubnetCommand { get; }
        public ICommand CancelSubnetScanCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand AcceptTransferCommand { get; }
        public ICommand RejectTransferCommand { get; }
        public ICommand RemoveTransferCommand { get; }
        public ICommand ShowHistoryCommand { get; }
        public ICommand ShowSettingsCommand { get; }

        public MainViewModel(
            IFileShareServiceManager serviceManager,
            IDialogService dialogService,
            IClassicDesktopStyleApplicationLifetime? appLifetime,
            SynchronizationContext uiContext,
            ILoggerFactory? loggerFactory = null,
            IAppSettingsService? settingsService = null,
            IThemeService? themeService = null)
        {
            _serviceManager = serviceManager ?? throw new ArgumentNullException(nameof(serviceManager));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _appLifetime = appLifetime;
            _uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
            _logger = loggerFactory?.CreateLogger<MainViewModel>() ?? NullLogger<MainViewModel>.Instance;

            // 设置与皮肤服务缺失时退化为「进程内默认值」，保证视图模型可独立创建（单元测试/设计器）
            _settingsService = settingsService ?? new InMemoryAppSettingsService();
            _themeService = themeService ?? new NoOpThemeService();

            Devices = new ObservableCollection<DeviceItemViewModel>();
            TransferTasks = new ObservableCollection<FileTransferViewModel>();
            SentTransferTasks = new ObservableCollection<FileTransferViewModel>();
            ReceivedTransferTasks = new ObservableCollection<FileTransferViewModel>();
            TransferTasks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTransferTasks));
            Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DeviceCountText));

            LocalDevice = _serviceManager.GetLocalDeviceInfo();
            _localDeviceId = LocalDevice?.DeviceId ?? string.Empty;
            if (LocalDevice is not null)
            {
                LocalDeviceSummary = $"本机：{LocalDevice.DeviceName} · {LocalDevice.IpAddress}:{LocalDevice.Port}";
            }

            // 默认填入本机所在网段，用户直接点「扫描」即可
            SubnetCidr = LocalNetworkHelper.GetSuggestedCidr() ?? "192.168.1.0/24";

            _serviceManager.OnDeviceDiscovered += OnDeviceDiscovered;
            _serviceManager.OnDeviceRemoved += OnDeviceRemoved;
            _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestSendAndReceive;
            _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
            _serviceManager.OnTransferCompleted += OnTransferCompleted;

            RefreshDevicesCommand = new RelayCommand(async () => await RefreshDevicesAsync());
            SendFileCommand = new RelayCommand(async () => await SendFileAsync());
            SendFilesCommand = new RelayCommand(async () => await SendFilesAsync());
            ConnectByAddressCommand = new RelayCommand(async () => await ConnectByAddressAsync());
            ScanSubnetCommand = new RelayCommand(async () => await ScanSubnetAsync());
            CancelSubnetScanCommand = new RelayCommand(CancelSubnetScan);
            ToggleFavoriteCommand = new RelayCommand<DeviceItemViewModel>(ToggleFavorite);
            AcceptTransferCommand = new RelayCommand<FileTransferViewModel>(AcceptTransfer);
            RejectTransferCommand = new RelayCommand<FileTransferViewModel>(RejectTransfer);
            RemoveTransferCommand = new RelayCommand<FileTransferViewModel>(RemoveTransfer);
            ShowHistoryCommand = new RelayCommand(ShowHistory);
            ShowSettingsCommand = new RelayCommand(ShowSettings);

            CurrentView = this;

            _ = InitializeAsync();
        }

        private void ShowHistory()
        {
            CurrentView = new HistoryViewModel(_serviceManager, _dialogService, () => CurrentView = this);
        }

        private void ShowSettings()
        {
            CurrentView = new SettingsViewModel(_settingsService, _themeService, _dialogService, () =>
            {
                CurrentView = this;
                RefreshLocalDeviceSummary();
            });
        }

        private void RefreshLocalDeviceSummary()
        {
            if (LocalDevice is not null)
            {
                LocalDeviceSummary = $"本机：{LocalDevice.DeviceName} · {LocalDevice.IpAddress}:{LocalDevice.Port}";
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

        private async Task RefreshDevicesAsync()
        {
            try
            {
                IsScanning = true;
                StatusMessage = "正在扫描设备...";
                await _serviceManager.RefreshDevicesAsync();
                await Task.Delay(2000); // 等待设备响应
                StatusMessage = $"扫描完成，当前在线 {Devices.Count} 台设备";
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

        /// <summary>
        /// 单文件发送（保留旧入口）。
        /// </summary>
        private async Task SendFileAsync()
        {
            if (SelectedDevice is null)
            {
                StatusMessage = "请先选择目标设备";
                return;
            }

            var target = SelectedDevice;
            var files = await _dialogService.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择要发送的文件",
                AllowMultiple = false
            });

            if (files.Count == 0)
            {
                return;
            }

            var filePath = files[0].Path.LocalPath;

            try
            {
                StatusMessage = $"准备发送文件: {Path.GetFileName(filePath)}";
                var success = await _serviceManager.SendFileAsync(filePath, target);

                if (success)
                {
                    StatusMessage = "文件发送成功";
                    _logger.LogInformation("文件发送成功: {Path} 到设备 {Device}", filePath, target.DeviceName);
                }
                else
                {
                    StatusMessage = "文件发送失败";
                    _logger.LogError("文件发送失败: {Path} 到设备 {Device}", filePath, target.DeviceName);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"发送文件失败: {ex.Message}";
                _logger.LogError(ex, "发送文件失败: {Path} 到设备 {Device}", filePath, target.DeviceName);
            }
        }

        /// <summary>
        /// 多文件发送（对应 LocalSend 的「一次拖入/选择多个文件」）。
        /// </summary>
        private async Task SendFilesAsync()
        {
            if (SelectedDevice is null)
            {
                StatusMessage = "请先选择目标设备";
                return;
            }

            var target = SelectedDevice;
            var files = await _dialogService.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择要发送的文件（可多选）",
                AllowMultiple = true
            });

            if (files.Count == 0)
            {
                return;
            }

            var paths = files.Select(file => file.Path.LocalPath).Where(File.Exists).ToList();
            if (paths.Count == 0)
            {
                StatusMessage = "没有可发送的文件";
                return;
            }

            var progress = new Progress<FileSendProgress>(item =>
            {
                StatusMessage = $"正在发送 ({item.Index}/{item.Total}): {Path.GetFileName(item.FilePath)}";
            });

            try
            {
                var allSucceeded = await _serviceManager.SendFilesAsync(paths, target, progress);
                StatusMessage = allSucceeded
                    ? $"{paths.Count} 个文件发送成功"
                    : "部分文件发送失败，详见任务列表";
            }
            catch (Exception ex)
            {
                StatusMessage = $"发送文件失败: {ex.Message}";
                _logger.LogError(ex, "批量发送文件失败");
            }
        }

        /// <summary>
        /// 手动输入 IP（可带端口）直连。跨子网或广播被拦截时使用。
        /// </summary>
        private async Task ConnectByAddressAsync()
        {
            if (IsConnecting)
            {
                return;
            }

            var input = ManualAddress?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                StatusMessage = "请输入设备 IP 地址，例如 192.168.1.20 或 192.168.1.20:5237";
                return;
            }

            if (!DeviceAddress.TryParse(input, _settingsService.Current.TransferPort, out _, out var error))
            {
                StatusMessage = error ?? "地址格式不正确";
                return;
            }

            IsConnecting = true;
            StatusMessage = $"正在连接 {input} ...";
            try
            {
                var device = await _serviceManager.ConnectByAddressAsync(input);
                if (device is null)
                {
                    StatusMessage = $"无法连接 {input}，请确认对端已启动、端口开放且网络可达";
                    _logger.LogWarning("手动连接失败: {Address}", input);
                    return;
                }

                AddOrUpdateDevice(device);
                SelectedDevice = device;
                StatusMessage = $"已连接 {device.DeviceName} ({device.IpAddress}:{device.Port})";
                ManualAddress = string.Empty;
            }
            catch (Exception ex)
            {
                StatusMessage = $"连接失败: {ex.Message}";
                _logger.LogError(ex, "手动连接失败: {Address}", input);
            }
            finally
            {
                IsConnecting = false;
            }
        }

        /// <summary>
        /// 扫描一个网段（跨子网场景下的批量发现）。
        /// </summary>
        private async Task ScanSubnetAsync()
        {
            if (IsSubnetScanning)
            {
                return;
            }

            if (!SubnetScanner.TryParseCidr(SubnetCidr, out _, out _, out var error))
            {
                StatusMessage = error ?? "网段格式不正确";
                return;
            }

            IsSubnetScanning = true;
            SubnetScanProgress = 0;
            _scanCts = new CancellationTokenSource();

            var progress = new Progress<SubnetScanProgress>(item =>
            {
                SubnetScanProgress = item.Percent;
                if (item.FoundDevice is not null)
                {
                    AddOrUpdateDevice(item.FoundDevice);
                }

                if (item.CurrentAddress is not null)
                {
                    StatusMessage = $"正在扫描 {item.CurrentAddress} ... ({item.Scanned}/{item.Total})";
                }
            });

            try
            {
                var devices = await _serviceManager.ScanSubnetAsync(SubnetCidr, progress, _scanCts.Token);
                StatusMessage = $"扫描完成，网段 {SubnetCidr} 内发现 {devices.Count} 台设备";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "已停止扫描";
            }
            catch (Exception ex)
            {
                StatusMessage = $"扫描失败: {ex.Message}";
                _logger.LogError(ex, "扫描网段失败: {Cidr}", SubnetCidr);
            }
            finally
            {
                IsSubnetScanning = false;
                SubnetScanProgress = 0;
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        private void CancelSubnetScan()
        {
            try
            {
                _scanCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 扫描已结束，忽略
            }
        }

        private void ToggleFavorite(DeviceItemViewModel? item)
        {
            if (item is null)
            {
                return;
            }

            var isFavorite = false;
            _settingsService.Update(settings => isFavorite = settings.ToggleFavorite(item.DeviceId));

            item.IsFavorite = isFavorite;

            // 收藏的设备置顶
            var index = Devices.IndexOf(item);
            if (isFavorite && index > 0)
            {
                Devices.RemoveAt(index);
                Devices.Insert(0, item);
            }

            StatusMessage = isFavorite
                ? $"已收藏 {item.DeviceName}；在设置中开启「自动接收收藏设备」后可免确认接收"
                : $"已取消收藏 {item.DeviceName}";
        }

        private async void AcceptTransfer(FileTransferViewModel? viewModel)
        {
            if (viewModel?.TransferId is null)
            {
                return;
            }

            // 设置了固定保存目录时不再每次询问
            var fixedDirectory = _settingsService.Current.SaveDirectory;
            if (!string.IsNullOrEmpty(fixedDirectory))
            {
                _serviceManager.HandleTransferRequest(viewModel.TransferId, true, fixedDirectory);
                StatusMessage = $"开始接收文件: {viewModel.FileName} (保存到: {fixedDirectory})";
                return;
            }

            var folder = await _dialogService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择文件保存位置",
                AllowMultiple = false
            });

            if (folder.Count == 0)
            {
                return;
            }

            var savePath = GetSafePath(folder[0].Path);
            _serviceManager.HandleTransferRequest(viewModel.TransferId, true, savePath);
            StatusMessage = $"开始接收文件: {viewModel.FileName} (保存到: {savePath})";
            _logger.LogInformation("开始接收文件: {File} (保存到: {Path})", viewModel.FileName, savePath);
        }

        private string GetSafePath(Uri uri)
        {
            string savePath;
            if (uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeFile)
            {
                savePath = uri.LocalPath; // 只有 file:// 协议才安全
            }
            else
            {
                // 普通本地路径或相对 URI，直接取原始字符串
                savePath = uri.OriginalString;
            }

            // 补全末尾分隔符
            if (!string.IsNullOrEmpty(savePath) && !savePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                savePath += Path.DirectorySeparatorChar;
            }

            return savePath;
        }

        private void RejectTransfer(FileTransferViewModel? viewModel)
        {
            if (viewModel?.TransferId == null)
            {
                return;
            }

            _serviceManager.HandleTransferRequest(viewModel.TransferId, false);
            StatusMessage = $"已拒绝文件: {viewModel.FileName}";
            _logger.LogInformation("已拒绝文件: {File}", viewModel.FileName);
        }

        /// <summary>
        /// 把设备合并到列表：已存在则刷新显示，不存在则按「收藏优先」插入。
        /// </summary>
        private void AddOrUpdateDevice(DeviceInfo device)
        {
            if (device is null || string.IsNullOrEmpty(device.DeviceId) || device.DeviceId == _localDeviceId)
            {
                return;
            }

            var existing = Devices.FirstOrDefault(item => item.DeviceId == device.DeviceId);
            if (existing is not null)
            {
                existing.RefreshDisplay();
                return;
            }

            var item = new DeviceItemViewModel(device, _settingsService.Current.IsFavorite(device.DeviceId));
            if (item.IsFavorite)
            {
                Devices.Insert(0, item);
            }
            else
            {
                Devices.Add(item);
            }

            StatusMessage = $"发现设备: {device.DeviceName}";
            _logger.LogInformation("发现设备: {Name} ({Id})", device.DeviceName, device.DeviceId);
        }

        private void OnDeviceDiscovered(DeviceInfo device)
        {
            if (device.DeviceId == _localDeviceId)
            {
                return;
            }

            RunOnUiThread(() => AddOrUpdateDevice(device));
        }

        private void OnDeviceRemoved(string deviceId)
        {
            if (deviceId == _localDeviceId)
            {
                return;
            }

            RunOnUiThread(() =>
            {
                var existing = Devices.FirstOrDefault(item => item.DeviceId == deviceId);
                if (existing is null)
                {
                    return;
                }

                Devices.Remove(existing);
                StatusMessage = $"设备已离线: {existing.DeviceName}";
                _logger.LogInformation("设备已离线: {Name} ({Id})", existing.DeviceName, deviceId);
            });
        }

        private async void RemoveTransfer(FileTransferViewModel? model)
        {
            if (model?.TransferId == null)
            {
                return;
            }

            switch (model.Status)
            {
                case TransferStatus.Pending:
                    _serviceManager.CancelTransfer(model.TransferId);
                    StatusMessage = $"已拒绝文件: {model.FileName}";
                    _logger.LogInformation("已拒绝文件: {File}", model.FileName);
                    break;
                case TransferStatus.Transferring:
                    var result = await _dialogService.ShowConfirmationDialogAsync("移除确认", "正在传输中，确定要移除吗？");
                    if (!result)
                    {
                        return;
                    }

                    _serviceManager.CancelTransfer(model.TransferId);
                    _logger.LogInformation("已取消文件传输: {File}", model.FileName);
                    break;
                case TransferStatus.Completed:
                case TransferStatus.Failed:
                case TransferStatus.Cancelled:
                    break;
                default:
                    break;
            }

            RemoveTransferFromLists(model);
        }

        private void RemoveTransferFromLists(FileTransferViewModel model)
        {
            TransferTasks.Remove(model);
            SentTransferTasks.Remove(model);
            ReceivedTransferTasks.Remove(model);
        }

        private void OnTransferRequestSendAndReceive(FileTransferInfo info)
        {
            var transferViewModel = FileTransferViewModel.FromModel(info);

            RunOnUiThread(() =>
            {
                TransferTasks.Insert(0, transferViewModel);

                if (info.SenderId == _localDeviceId)
                {
                    SentTransferTasks.Insert(0, transferViewModel);
                    StatusMessage = $"准备发送文件: {info.FileName}";
                    _logger.LogInformation("准备发送文件: {File} ({Id})", info.FileName, info.TransferId);
                }
                else
                {
                    ReceivedTransferTasks.Insert(0, transferViewModel);
                    StatusMessage = $"收到文件传输请求: {info.FileName}";
                    _logger.LogInformation("收到文件传输请求: {File} ({Id})", info.FileName, info.TransferId);
                }
            });

            // Quick Save：收藏设备发来的文件自动接收
            if (info.Direction == TransferDirection.Receive &&
                info.SenderId != _localDeviceId &&
                _settingsService.Current.AutoAcceptFromFavorites &&
                _settingsService.Current.IsFavorite(info.SenderId))
            {
                _serviceManager.HandleTransferRequest(info.TransferId, true, _settingsService.Current.SaveDirectory);
                RunOnUiThread(() => StatusMessage = $"已自动接收收藏设备的文件: {info.FileName}");
                _logger.LogInformation("自动接收收藏设备文件: {File} ({Id})", info.FileName, info.TransferId);
            }
        }

        private void OnTransferProgressUpdated(FileTransferInfo updatedInfo)
        {
            RunOnUiThread(() =>
            {
                var viewModel = TransferTasks.FirstOrDefault(task => task.TransferId == updatedInfo.TransferId);
                if (viewModel is null)
                {
                    return;
                }

                viewModel.Status = updatedInfo.Status;
                viewModel.FileSize = updatedInfo.FileSize;
                viewModel.TransferredSize = updatedInfo.TransferredSize;
                viewModel.ProgressPercentage = updatedInfo.ProgressPercentage;
                viewModel.SpeedBytesPerSecond = updatedInfo.TransferRateBytesPerSec;

                StatusMessage = $"正在传输: {updatedInfo.FileName} ({updatedInfo.ProgressPercentage:F1}%)";
            });
        }

        private void OnTransferCompleted(FileTransferInfo updatedInfo, string? errorMessage)
        {
            RunOnUiThread(() =>
            {
                var viewModel = TransferTasks.FirstOrDefault(task => task.TransferId == updatedInfo.TransferId);
                if (viewModel is not null)
                {
                    viewModel.Status = updatedInfo.Status;
                    viewModel.FileSize = updatedInfo.FileSize;
                    viewModel.TransferredSize = updatedInfo.TransferredSize;
                    viewModel.ProgressPercentage = updatedInfo.ProgressPercentage;
                    viewModel.SpeedBytesPerSecond = 0;
                }

                switch (updatedInfo.Status)
                {
                    case TransferStatus.Completed:
                        StatusMessage = $"文件传输完成: {updatedInfo.FileName}";
                        _logger.LogInformation("文件传输完成: {File} ({Id})", updatedInfo.FileName, updatedInfo.TransferId);
                        break;
                    case TransferStatus.Failed:
                        StatusMessage = $"文件传输失败: {errorMessage ?? string.Empty}";
                        _logger.LogError("文件传输失败: {File} ({Id}), {Error}", updatedInfo.FileName, updatedInfo.TransferId, errorMessage);
                        break;
                    case TransferStatus.Cancelled:
                        StatusMessage = $"文件传输取消: {errorMessage ?? string.Empty}";
                        _logger.LogInformation("文件传输取消: {File} ({Id}), {Error}", updatedInfo.FileName, updatedInfo.TransferId, errorMessage);
                        break;
                    default:
                        StatusMessage = errorMessage ?? string.Empty;
                        break;
                }
            });
        }

        /// <summary>
        /// 在 UI 线程同步执行（结构性变更：增删列表项）。
        /// </summary>
        private void RunOnUiThread(Action action)
        {
            try
            {
                if (SynchronizationContext.Current == _uiContext)
                {
                    action();
                    return;
                }

                _uiContext.Send(_ => action(), null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "在 UI 线程执行操作失败");
            }
        }

        public void Dispose()
        {
            _serviceManager.OnDeviceDiscovered -= OnDeviceDiscovered;
            _serviceManager.OnDeviceRemoved -= OnDeviceRemoved;
            _serviceManager.OnTransferRequestSendAndReceive -= OnTransferRequestSendAndReceive;
            _serviceManager.OnTransferProgressUpdated -= OnTransferProgressUpdated;
            _serviceManager.OnTransferCompleted -= OnTransferCompleted;
            _serviceManager.Dispose();
        }
    }

    /// <summary>
    /// 默认（无持久化）设置服务：仅在未注入 <see cref="IAppSettingsService"/> 时使用（设计器/单元测试）。
    /// </summary>
    internal sealed class InMemoryAppSettingsService : IAppSettingsService
    {
        private AppSettings _current = new();

        public AppSettings Current => _current;

        public string SettingsFilePath => string.Empty;

        public event EventHandler? Changed;

        public void Update(Action<AppSettings> mutate)
        {
            mutate(_current);
            _current.Normalize();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Save()
        {
        }

        public bool Reload() => false;
    }

    /// <summary>
    /// 空实现皮肤服务：未注入 <see cref="IThemeService"/> 时使用。
    /// </summary>
    internal sealed class NoOpThemeService : IThemeService
    {
        public AppSkin Current { get; private set; } = AppSkin.Win7;

        public void Apply(AppSkin skin) => Current = skin;
    }
}
