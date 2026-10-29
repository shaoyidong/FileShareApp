using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Network.Discovery;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using FileShare.Desktop.Views;
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
using FileShare.Desktop.Localization;
using LocalSend.Core.Http.DtoV2;

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
        private readonly IToastService? _toastService;
        private readonly ITrayService? _trayService;
        private readonly ILocalSendInteropHost? _localSendHost;
        private readonly Dictionary<string, LocalSendPeer> _localSendPeers = new();
        private readonly SynchronizationContext _uiContext;
        private readonly IClassicDesktopStyleApplicationLifetime? _appLifetime;
        private readonly ILogger<MainViewModel> _logger;
        private readonly string _localDeviceId;

        private CancellationTokenSource? _scanCts;
        private TransferQueueControl? _sendQueue;

        [ObservableProperty]
        private bool _isSending;

        [ObservableProperty]
        private bool _isSendPaused;

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
        private string _statusMessage = Loc.Get("准备就绪");

        [ObservableProperty]
        private object? _currentView;
        
        private DeviceItemViewModel? _selectedItem;

        public DeviceItemViewModel? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetProperty(ref _selectedItem, value))
                {
                    OnPropertyChanged(nameof(SelectedDevice));
                    OnPropertyChanged(nameof(IsDeviceSelected));
                }
            }
        }

        [ObservableProperty]
        private string _localDeviceSummary = string.Empty;

        /// <summary>本机设备信息。</summary>
        public DeviceInfo? LocalDevice { get; }

        /// <summary>窗口标题。</summary>
        public string WindowTitle => LocalDevice is null
            ? Loc.Get("FileShare 局域网传输")
            : Loc.F("FileShare 局域网传输 - {0} ({1})", LocalDevice.DeviceName, LocalDevice.IpAddress);

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
        public string DeviceCountText => Loc.F("共 {0} 台", Devices.Count);

        public ObservableCollection<DeviceItemViewModel> Devices { get; }
        public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }

        /// <summary>待发送队列（可重排）。</summary>
        public ObservableCollection<SendQueueItemViewModel> SendQueue { get; } = new();

        /// <summary>队列是否有待发送项。</summary>
        public bool HasQueue => SendQueue.Count > 0;

        /// <summary>队列数量文本。</summary>
        public string QueueCountText => Loc.F("待发送 {0} 项", SendQueue.Count);

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand SendFilesCommand { get; }
        public ICommand SendFolderCommand { get; }
        public ICommand SendTextCommand { get; }
        public ICommand ConnectByAddressCommand { get; }
        public ICommand ScanSubnetCommand { get; }
        public ICommand CancelSubnetScanCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand AcceptTransferCommand { get; }
        public ICommand RejectTransferCommand { get; }
        public ICommand RemoveTransferCommand { get; }
        public ICommand RetryTransferCommand { get; }
        public ICommand ShowHistoryCommand { get; }
        public ICommand ShowSettingsCommand { get; }
        public ICommand PauseSendCommand { get; }
        public ICommand ResumeSendCommand { get; }
        public ICommand CancelSendCommand { get; }
        public ICommand MoveQueueUpCommand { get; }
        public ICommand MoveQueueDownCommand { get; }
        public ICommand RemoveQueueItemCommand { get; }
        public ICommand ClearQueueCommand { get; }

        public MainViewModel(
            IFileShareServiceManager serviceManager,
            IDialogService dialogService,
            IClassicDesktopStyleApplicationLifetime? appLifetime,
            SynchronizationContext uiContext,
            ILoggerFactory? loggerFactory = null,
            IAppSettingsService? settingsService = null,
            IThemeService? themeService = null,
            IToastService? toastService = null,
            ITrayService? trayService = null,
        ILocalSendInteropHost? localSendHost = null)
        {
            _serviceManager = serviceManager ?? throw new ArgumentNullException(nameof(serviceManager));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _appLifetime = appLifetime;
            _uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
            _logger = loggerFactory?.CreateLogger<MainViewModel>() ?? NullLogger<MainViewModel>.Instance;

            // 设置与皮肤服务缺失时退化为「进程内默认值」，保证视图模型可独立创建（单元测试/设计器）
            _settingsService = settingsService ?? new InMemoryAppSettingsService();
            _themeService = themeService ?? new NoOpThemeService();
            _toastService = toastService;
            _trayService = trayService;
            _localSendHost = localSendHost;

            Devices = new ObservableCollection<DeviceItemViewModel>();
            TransferTasks = new ObservableCollection<FileTransferViewModel>();
            SentTransferTasks = new ObservableCollection<FileTransferViewModel>();
            ReceivedTransferTasks = new ObservableCollection<FileTransferViewModel>();
            TransferTasks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTransferTasks));
            Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DeviceCountText));
            if (_localSendHost is not null)
            {
                _localSendHost.PeerDiscovered += OnLocalSendPeerDiscovered;
                foreach (var peer in _localSendHost.Peers)
                {
                    RegisterLocalSendPeer(peer);
                }
            }

            SendQueue.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasQueue));
                OnPropertyChanged(nameof(QueueCountText));
            };

            LocalDevice = _serviceManager.GetLocalDeviceInfo();
            _localDeviceId = LocalDevice?.DeviceId ?? string.Empty;
            if (LocalDevice is not null)
            {
                LocalDeviceSummary = Loc.F("本机：{0} · {1}:{2}", LocalDevice.DeviceName, LocalDevice.IpAddress, LocalDevice.Port);
            }

            // 默认填入本机所在网段，用户直接点「扫描」即可
            SubnetCidr = LocalNetworkHelper.GetSuggestedCidr() ?? "192.168.1.0/24";

            _serviceManager.OnDeviceDiscovered += OnDeviceDiscovered;
            _serviceManager.OnDeviceRemoved += OnDeviceRemoved;
            _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestSendAndReceive;
            _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
            _serviceManager.OnTransferCompleted += OnTransferCompleted;
            _settingsService.Changed += OnSettingsChanged;
            Loc.LanguageChanged += OnLanguageChanged;
            _serviceManager.OnFirstUseTrusted += OnFirstUseTrusted;
            _serviceManager.OnFingerprintMismatch += OnFingerprintMismatch;

            RefreshDevicesCommand = new RelayCommand(async () => await RefreshDevicesAsync());
            SendFileCommand = new RelayCommand(async () => await SendFileAsync());
            SendFilesCommand = new RelayCommand(async () => await SendFilesAsync());
            SendFolderCommand = new RelayCommand(async () => await SendFolderAsync());
            SendTextCommand = new RelayCommand(async () => await SendTextAsync());
            ConnectByAddressCommand = new RelayCommand(async () => await ConnectByAddressAsync());
            ScanSubnetCommand = new RelayCommand(async () => await ScanSubnetAsync());
            CancelSubnetScanCommand = new RelayCommand(CancelSubnetScan);
            ToggleFavoriteCommand = new RelayCommand<DeviceItemViewModel>(ToggleFavorite);
            AcceptTransferCommand = new RelayCommand<FileTransferViewModel>(AcceptTransfer);
            RejectTransferCommand = new RelayCommand<FileTransferViewModel>(RejectTransfer);
            RemoveTransferCommand = new RelayCommand<FileTransferViewModel>(RemoveTransfer);
            RetryTransferCommand = new RelayCommand<FileTransferViewModel>(RetryTransfer);
            ShowHistoryCommand = new RelayCommand(ShowHistory);
            ShowSettingsCommand = new RelayCommand(ShowSettings);
            PauseSendCommand = new RelayCommand(PauseSending);
            ResumeSendCommand = new RelayCommand(ResumeSending);
            CancelSendCommand = new RelayCommand(CancelSending);
            MoveQueueUpCommand = new RelayCommand<SendQueueItemViewModel>(item => MoveQueueItem(item, -1));
            MoveQueueDownCommand = new RelayCommand<SendQueueItemViewModel>(item => MoveQueueItem(item, 1));
            RemoveQueueItemCommand = new RelayCommand<SendQueueItemViewModel>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue);

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
                LocalDeviceSummary = Loc.F("本机：{0} · {1}:{2}", LocalDevice.DeviceName, LocalDevice.IpAddress, LocalDevice.Port);
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                await _serviceManager.StartServicesAsync();
                StatusMessage = Loc.Get("准备就绪");
                _logger.LogInformation("服务启动成功");
            }
            catch (Exception ex)
            {
                // 端口被占用是最常见的启动失败原因（另一实例已在运行 / 端口被其它程序占用）
                StatusMessage = ex is System.Net.Sockets.SocketException
                    ? Loc.F("启动服务失败：端口被占用（{0}）。请关闭另一个实例，或在设置里换一个端口。", _settingsService.Current.TransferPort)
                    : Loc.F("启动服务失败: {0}", ex.Message);
                _logger.LogError(ex, "启动服务失败");
            }
        }

        private async Task RefreshDevicesAsync()
        {
            try
            {
                IsScanning = true;
                StatusMessage = Loc.Get("正在扫描设备...");
                await _serviceManager.RefreshDevicesAsync();
                await Task.Delay(2000); // 等待设备响应
                StatusMessage = Loc.F("扫描完成，当前在线 {0} 台设备", Devices.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("扫描设备失败: {0}", ex.Message);
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
                StatusMessage = Loc.Get("请先选择目标设备");
                return;
            }

            var target = SelectedDevice;
            var files = await _dialogService.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Loc.Get("选择要发送的文件"),
                AllowMultiple = false
            });

            if (files.Count == 0)
            {
                return;
            }

            var filePath = files[0].Path.LocalPath;

            try
            {
                StatusMessage = Loc.F("准备发送文件: {0}", Path.GetFileName(filePath));
                var success = await _serviceManager.SendFileAsync(filePath, target);

                if (success)
                {
                    StatusMessage = Loc.Get("文件发送成功");
                    _logger.LogInformation("文件发送成功: {Path} 到设备 {Device}", filePath, target.DeviceName);
                }
                else
                {
                    StatusMessage = Loc.Get("文件发送失败");
                    _logger.LogError("文件发送失败: {Path} 到设备 {Device}", filePath, target.DeviceName);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("发送文件失败: {0}", ex.Message);
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
                StatusMessage = Loc.Get("请先选择目标设备");
                return;
            }

            var target = SelectedDevice;
            var files = await _dialogService.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Loc.Get("选择要发送的文件（可多选）"),
                AllowMultiple = true
            });

            if (files.Count == 0)
            {
                return;
            }

            var paths = files.Select(file => file.Path.LocalPath).Where(File.Exists).ToList();
            if (paths.Count == 0)
            {
                StatusMessage = Loc.Get("没有可发送的文件");
                return;
            }

            var progress = new Progress<FileSendProgress>(item =>
            {
                StatusMessage = Loc.F("正在发送 ({0}/{1}): {2}", item.Index, item.Total, Path.GetFileName(item.FilePath));
            });

            EnqueueFiles(paths);
            await StartQueueIfIdleAsync();
        }

        /// <summary>
        /// 把文件加入发送队列（文件夹会展开为其中的文件，保留相对路径）。
        /// </summary>
        private void EnqueueFiles(IEnumerable<string> filePaths, string? folderRoot = null, string? folderName = null)
        {
            foreach (var path in filePaths)
            {
                string? relativePath = null;
                if (!string.IsNullOrEmpty(folderRoot) && !string.IsNullOrEmpty(folderName))
                {
                    relativePath = Path.Combine(folderName, Path.GetRelativePath(folderRoot, path));
                }

                SendQueue.Add(new SendQueueItemViewModel(path, relativePath));
            }
        }

        private void EnqueueFolder(string folderPath)
        {
            try
            {
                var root = new DirectoryInfo(folderPath);
                var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Select(file => file.FullName)
                    .ToList();

                if (files.Count == 0)
                {
                    StatusMessage = Loc.Get("文件夹为空，无需发送");
                    return;
                }

                EnqueueFiles(files, folderPath, root.Name);
                StatusMessage = Loc.F("已加入队列：{0} 个文件", files.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("读取文件夹失败: {0}", ex.Message);
                _logger.LogError(ex, "读取文件夹失败: {Path}", folderPath);
            }
        }

        private void MoveQueueItem(SendQueueItemViewModel? item, int offset)
        {
            if (item is null)
            {
                return;
            }

            var index = SendQueue.IndexOf(item);
            var target = index + offset;

            if (index < 0 || target < 0 || target >= SendQueue.Count)
            {
                return;
            }

            SendQueue.Move(index, target);
        }

        private void RemoveQueueItem(SendQueueItemViewModel? item)
        {
            if (item is not null)
            {
                SendQueue.Remove(item);
            }
        }

        private void ClearQueue()
        {
            if (IsSending)
            {
                StatusMessage = Loc.Get("正在发送，请先暂停或取消后再清空队列");
                return;
            }

            SendQueue.Clear();
            StatusMessage = Loc.Get("已清空发送队列");
        }

        /// <summary>
        /// 按当前队列顺序逐项发送（暂停后可调整顺序，重排立即生效）。
        /// </summary>
        private async Task StartQueueIfIdleAsync()
        {
            if (IsSending || SendQueue.Count == 0)
            {
                return;
            }

            var target = SelectedDevice;
            if (target is null)
            {
                StatusMessage = Loc.Get("请先选择目标设备，选择后可继续发送队列");
                return;
            }

            var total = SendQueue.Count;
            var index = 0;
            var failed = 0;
            var queue = BeginSendQueue();

            try
            {
                while (SendQueue.Count > 0)
                {
                    if (queue.IsCancelled)
                    {
                        break;
                    }

                    var item = SendQueue[0];
                    index++;
                    StatusMessage = Loc.F("正在发送 ({0}/{1}): {2}", index, total, item.DisplayName);

                    bool success;
                    try
                    {
                        success = await SendSingleToTargetAsync(item.Path, item.RelativePath, queue, null);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "发送文件失败: {Path}", item.Path);
                        success = false;
                    }

                    if (!success)
                    {
                        failed++;
                    }

                    SendQueue.Remove(item);
                }

                StatusMessage = queue.IsCancelled
                    ? Loc.Get("已取消发送（剩余项目仍在队列中）")
                    : failed == 0
                        ? Loc.F("{0} 个文件发送成功", total)
                        : Loc.F("发送完成：成功 {0} 个，失败 {1} 个", total - failed, failed);
            }
            finally
            {
                EndSendQueue();
            }
        }

        /// <summary>
        /// 开始一批发送：创建队列控制器，返回给 SendFilesAsync / SendFolderAsync。
        /// </summary>
        private TransferQueueControl BeginSendQueue()
        {
            EndSendQueue();

            _sendQueue = new TransferQueueControl();
            IsSending = true;
            IsSendPaused = false;
            return _sendQueue;
        }

        /// <summary>结束一批发送并释放队列控制器。</summary>
        private void EndSendQueue()
        {
            _sendQueue?.Dispose();
            _sendQueue = null;
            IsSending = false;
            IsSendPaused = false;
        }

        private void PauseSending()
        {
            if (_sendQueue is null || _sendQueue.IsPaused)
            {
                return;
            }

            _sendQueue.Pause();
            IsSendPaused = true;
            StatusMessage = Loc.Get("已暂停：当前文件传完后暂停，点击「继续」恢复");
        }

        private void ResumeSending()
        {
            if (_sendQueue is null || !_sendQueue.IsPaused)
            {
                return;
            }

            _sendQueue.Resume();
            IsSendPaused = false;
            StatusMessage = Loc.Get("已继续发送");
        }

        private void CancelSending()
        {
            if (_sendQueue is null)
            {
                return;
            }

            _sendQueue.Cancel();
            StatusMessage = Loc.Get("正在取消发送...");
        }
        /// <summary>
        /// 发送整个文件夹（保留目录结构）。
        /// </summary>
        private async Task SendFolderAsync()
        {
            if (SelectedDevice is null)
            {
                StatusMessage = Loc.Get("请先选择目标设备");
                return;
            }

            var target = SelectedDevice;
            var folders = await _dialogService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = Loc.Get("选择要发送的文件夹"),
                AllowMultiple = false
            });

            if (folders.Count == 0)
            {
                return;
            }

            var uri = folders[0].Path;
            var folderPath = uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeFile ? uri.LocalPath : uri.OriginalString;

            var progress = new Progress<FileSendProgress>(item =>
            {
                StatusMessage = Loc.F("正在发送文件夹 ({0}/{1}): {2}", item.Index, item.Total, Path.GetFileName(item.FilePath));
            });

            EnqueueFolder(folderPath);
            await StartQueueIfIdleAsync();
        }

        /// <summary>
        /// 发送文本（对端收到一个 .txt 文件）。
        /// </summary>
        private async Task SendTextAsync()
        {
            if (SelectedDevice is null)
            {
                StatusMessage = Loc.Get("请先选择目标设备");
                return;
            }

            var target = SelectedDevice;
            var owner = _appLifetime?.MainWindow;

            if (owner is null)
            {
                StatusMessage = Loc.Get("当前窗口不可用，无法打开文本编辑框");
                return;
            }

            var window = new Views.SendTextWindow(target.DeviceName);
            var text = await window.ShowDialog<string?>(owner);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                StatusMessage = Loc.F("正在发送文本（{0} 字符）...", text.Length);
                var success = SelectedLocalSendPeer is { } localSendPeer && _localSendHost is not null
                    ? await SendTextToLocalSendAsync(localSendPeer, text)
                    : await _serviceManager.SendTextAsync(text, target);
                StatusMessage = success ? Loc.Get("文本已发送") : Loc.Get("文本发送失败");
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("发送文本失败: {0}", ex.Message);
                _logger.LogError(ex, "发送文本失败");
            }
        }

        /// <summary>
        /// LocalSend 只支持文件传输：把文本写成临时文件再发（与自有协议的 .txt 行为一致）。
        /// </summary>
        private async Task<bool> SendTextToLocalSendAsync(LocalSendPeer peer, string text)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "FileShare.Text");
            Directory.CreateDirectory(tempDirectory);
            var fileName = $"文本_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            var filePath = Path.Combine(tempDirectory, fileName);

            try
            {
                await File.WriteAllTextAsync(filePath, text, System.Text.Encoding.UTF8);

                var result = await _localSendHost!
                    .SendFileAsync(peer, filePath, fileName, null)
                    .ConfigureAwait(true);

                if (!result.Success && !string.IsNullOrEmpty(result.Error))
                {
                    StatusMessage = Loc.F("发送失败: {0}", result.Error!);
                }

                return result.Success;
            }
            finally
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception)
                {
                    // 临时文件清理失败不影响结果
                }
            }
        }

        /// <summary>
        /// 处理拖拽进来的文件/文件夹（由 MainView 的 DragDrop 事件调用）。
        /// </summary>
        public async Task HandleDroppedPathsAsync(IReadOnlyList<string> paths)
        {
            if (paths is null || paths.Count == 0)
            {
                return;
            }

            // 未选设备但只发现了一台时，自动选中，省一次点击
            if (SelectedDevice is null)
            {
                if (Devices.Count == 1)
                {
                    SelectedItem = Devices[0];
                }
                else
                {
                    StatusMessage = Loc.Get("请先在左侧选择目标设备，再拖入文件");
                    return;
                }
            }

            var target = SelectedDevice!;
            var files = paths.Where(File.Exists).ToList();
            var folders = paths.Where(Directory.Exists).ToList();

            if (files.Count == 0 && folders.Count == 0)
            {
                StatusMessage = Loc.Get("拖入的内容无法识别");
                return;
            }

            var progress = new Progress<FileSendProgress>(item =>
            {
                StatusMessage = Loc.F("正在发送 ({0}/{1}): {2}", item.Index, item.Total, Path.GetFileName(item.FilePath));
            });

            if (files.Count > 0)
            {
                EnqueueFiles(files);
            }

            foreach (var folder in folders)
            {
                EnqueueFolder(folder);
            }

            await StartQueueIfIdleAsync();
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
                StatusMessage = Loc.Get("请输入设备 IP 地址，例如 192.168.1.20 或 192.168.1.20:5237");
                return;
            }

            if (!DeviceAddress.TryParse(input, _settingsService.Current.TransferPort, out _, out var error))
            {
                StatusMessage = error ?? Loc.Get("地址格式不正确");
                return;
            }

            IsConnecting = true;
            StatusMessage = Loc.F("正在连接 {0} ...", input);
            try
            {
                var device = await _serviceManager.ConnectByAddressAsync(input);
                if (device is null)
                {
                    // 兜底：对端可能是官方 LocalSend 客户端（HTTP v2），用 /info 探测
                    var probed = await TryProbeLocalSendAsync(input).ConfigureAwait(true);
                    if (probed is not null)
                    {
                        RegisterLocalSendPeer(probed);
                        SelectedItem = Devices.FirstOrDefault(item => item.DeviceId == probed.DeviceId);
                        StatusMessage = Loc.F("已连接 {0} ({1}:{2})", probed.Alias, probed.IpAddress, probed.Port);
                        ManualAddress = string.Empty;
                        return;
                    }

                    StatusMessage = Loc.F("无法连接 {0}，请确认对端已启动、端口开放且网络可达", input);
                    _logger.LogWarning("手动连接失败: {Address}", input);
                    return;
                }

                AddOrUpdateDevice(device);
                SelectedDevice = device;
                StatusMessage = Loc.F("已连接 {0} ({1}:{2})", device.DeviceName, device.IpAddress, device.Port);
                ManualAddress = string.Empty;
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("连接失败: {0}", ex.Message);
                _logger.LogError(ex, "手动连接失败: {Address}", input);
            }
            finally
            {
                IsConnecting = false;
            }
        }

        /// <summary>
        /// 探测目标是否为 LocalSend 设备（输入可带端口，默认 53317）。
        /// </summary>
        private async Task<LocalSendPeer?> TryProbeLocalSendAsync(string input)
        {
            if (_localSendHost is null)
            {
                return null;
            }

            var port = 53317;
            var address = input.Trim();

            var colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address[(colon + 1)..], out var explicitPort))
            {
                address = address[..colon];
                port = explicitPort;
            }

            try
            {
                return await _localSendHost.ProbePeerAsync(address, port).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "探测 LocalSend 设备失败: {Address}:{Port}", address, port);
                return null;
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
                StatusMessage = error ?? Loc.Get("网段格式不正确");
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
                    StatusMessage = Loc.F("正在扫描 {0} ... ({1}/{2})", item.CurrentAddress, item.Scanned, item.Total);
                }
            });

            try
            {
                var devices = await _serviceManager.ScanSubnetAsync(SubnetCidr, progress, _scanCts.Token);
                StatusMessage = Loc.F("扫描完成，网段 {0} 内发现 {1} 台设备", SubnetCidr, devices.Count);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = Loc.Get("已停止扫描");
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("扫描失败: {0}", ex.Message);
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
                ? Loc.F("已收藏 {0}；在设置中开启「自动接收收藏设备」后可免确认接收", item.DeviceName)
                : Loc.F("已取消收藏 {0}", item.DeviceName);
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
                StatusMessage = Loc.F("开始接收文件: {0} (保存到: {1})", viewModel.FileName, fixedDirectory);
                return;
            }

            var folder = await _dialogService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = Loc.Get("选择文件保存位置"),
                AllowMultiple = false
            });

            if (folder.Count == 0)
            {
                return;
            }

            var savePath = GetSafePath(folder[0].Path);
            _serviceManager.HandleTransferRequest(viewModel.TransferId, true, savePath);
            StatusMessage = Loc.F("开始接收文件: {0} (保存到: {1})", viewModel.FileName, savePath);
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
            StatusMessage = Loc.F("已拒绝文件: {0}", viewModel.FileName);
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

            StatusMessage = Loc.F("发现设备: {0}", device.DeviceName);
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
                StatusMessage = Loc.F("设备已离线: {0}", existing.DeviceName);
                _logger.LogInformation("设备已离线: {Name} ({Id})", existing.DeviceName, deviceId);
            });
        }

        /// <summary>
        /// 重试一次失败的发送。接收端会保留未完成的 .part，因此可自动续传。
        /// </summary>
        private async void RetryTransfer(FileTransferViewModel? model)
        {
            if (model?.SourcePath is null || model.TargetDeviceId is null)
            {
                return;
            }

            if (!File.Exists(model.SourcePath))
            {
                StatusMessage = Loc.Get("找不到该文件（可能已被移动或删除）。");
                return;
            }

            if (_localSendPeers.TryGetValue(model.TargetDeviceId, out var localSendPeer) && _localSendHost is not null)
            {
                try
                {
                    model.Status = TransferStatus.Pending;
                    StatusMessage = Loc.F("准备发送文件: {0}", model.FileName);

                    var localResult = await _localSendHost.SendFileAsync(localSendPeer, model.SourcePath, null, null);
                    if (!localResult.Success)
                    {
                        model.Status = TransferStatus.Failed;
                        StatusMessage = Loc.F("发送失败: {0}", localResult.Error ?? string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    model.Status = TransferStatus.Failed;
                    StatusMessage = Loc.F("发送文件失败: {0}", ex.Message);
                    _logger.LogError(ex, "重试发送（LocalSend）失败: {Path}", model.SourcePath);
                }

                return;
            }

            var target = Devices.FirstOrDefault(item => item.DeviceId == model.TargetDeviceId)?.Device;
            target ??= _serviceManager.GetLocalDeviceInfo() is { } local && local.DeviceId == model.TargetDeviceId
                ? local
                : null;

            if (target is null)
            {
                StatusMessage = Loc.Get("设备已离线: {0}").Replace("{0}", model.TargetDeviceId);
                return;
            }

            try
            {
                model.Status = TransferStatus.Pending;
                StatusMessage = Loc.F("准备发送文件: {0}", model.FileName);

                var success = await _serviceManager.SendSingleAsync(model.SourcePath, target, null, null);

                if (!success)
                {
                    model.Status = TransferStatus.Failed;
                    StatusMessage = Loc.Get("文件发送失败");
                }
            }
            catch (Exception ex)
            {
                model.Status = TransferStatus.Failed;
                StatusMessage = Loc.F("发送文件失败: {0}", ex.Message);
                _logger.LogError(ex, "重试发送失败: {Path}", model.SourcePath);
            }
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
                    StatusMessage = Loc.F("已拒绝文件: {0}", model.FileName);
                    _logger.LogInformation("已拒绝文件: {File}", model.FileName);
                    break;
                case TransferStatus.Transferring:
                    var result = await _dialogService.ShowConfirmationDialogAsync(Loc.Get("移除确认"), Loc.Get("正在传输中，确定要移除吗？"));
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
                    StatusMessage = Loc.F("准备发送文件: {0}", info.FileName);
                    _logger.LogInformation("准备发送文件: {File} ({Id})", info.FileName, info.TransferId);
                }
                else
                {
                    ReceivedTransferTasks.Insert(0, transferViewModel);
                    StatusMessage = Loc.F("收到文件传输请求: {0}", info.FileName);
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
                RunOnUiThread(() => StatusMessage = Loc.F("已自动接收收藏设备的文件: {0}", info.FileName));
                _logger.LogInformation("自动接收收藏设备文件: {File} ({Id})", info.FileName, info.TransferId);
                return;
            }

            // 接收请求用模态窗口确认（P0）：任务列表里的内联按钮保留，但 60 秒窗口很容易被错过
            if (info.Direction == TransferDirection.Receive && info.SenderId != _localDeviceId)
            {
                RunOnUiThread(() => _ = ShowReceiveRequestDialogAsync(info));
            }
        }

        /// <summary>
        /// 显示接收确认窗口，并在用户选择后把结果回传给传输服务。
        /// </summary>
        private async Task ShowReceiveRequestDialogAsync(FileTransferInfo info)
        {
            var owner = _appLifetime?.MainWindow;
            if (owner is null)
            {
                // 没有主窗口（单元测试/无界面场景）：保持内联按钮的默认行为
                return;
            }

            try
            {
                owner.Show();
                if (owner.WindowState == Avalonia.Controls.WindowState.Minimized)
                {
                    owner.WindowState = Avalonia.Controls.WindowState.Normal;
                }

                owner.Activate();

                var senderName = Devices.FirstOrDefault(item => item.DeviceId == info.SenderId)?.DeviceName
                                 ?? info.SenderId;

                // 托盘/后台时用通知卡片提醒，点击回到主窗口
                _toastService?.Show(
                    Loc.Get("收到文件传输请求"),
                    Loc.F("{0} 想发送：{1}", senderName, info.FileName),
                    ActivateMainWindow);

                var window = new Views.ReceiveRequestWindow(info, _settingsService.Current.SaveDirectory, senderName);
                var result = await window.ShowDialog<ReceiveRequestResult?>(owner);

                if (result is null)
                {
                    return;
                }

                if (result.AlwaysAccept)
                {
                    // Update 会触发 Changed，由 OnSettingsChanged 统一刷新星标与排序
                    _settingsService.Update(settings => settings.ToggleFavorite(info.SenderId));
                }

                var savePath = _settingsService.Current.SaveDirectory;
                _serviceManager.HandleTransferRequest(info.TransferId, result.Accept, savePath);

                StatusMessage = result.Accept
                    ? Loc.F("已接受文件: {0}", info.FileName)
                    : Loc.F("已拒绝文件: {0}", info.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "显示接收确认窗口失败，回退为内联确认");
            }
        }

        private void OnLocalSendPeerDiscovered(LocalSendPeer peer)
        {
            RunOnUiThread(() => RegisterLocalSendPeer(peer));
        }

        /// <summary>把 LocalSend 设备并入设备列表（已存在则忽略）。</summary>
        private void RegisterLocalSendPeer(LocalSendPeer peer)
        {
            if (!_localSendPeers.TryAdd(peer.DeviceId, peer))
            {
                return;
            }

            if (Devices.Any(item => item.DeviceId == peer.DeviceId))
            {
                return;
            }

            var device = new DeviceInfo
            {
                DeviceId = peer.DeviceId,
                DeviceName = peer.Alias,
                IpAddress = peer.IpAddress,
                Port = peer.Port,
                DeviceType = peer.DeviceType switch
                {
                    LocalSend.Core.Model.DeviceType.Mobile => DeviceType.Mobile,
                    LocalSend.Core.Model.DeviceType.Web => DeviceType.Desktop,
                    _ => DeviceType.Desktop
                },
                SupportsTls = peer.Protocol == ProtocolTypeV2.Https
            };

            Devices.Add(new DeviceItemViewModel(device, isFavorite: false, isLocalSend: true));
            _logger.LogInformation("已把 LocalSend 设备加入列表: {Name} ({Ip}:{Port}, 类型={Type})", peer.Alias, peer.IpAddress, peer.Port, device.DeviceType);
        }

        /// <summary>当前选中的设备是否是 LocalSend 设备。</summary>
        private LocalSendPeer? SelectedLocalSendPeer =>
            SelectedItem is { IsLocalSend: true } item && _localSendPeers.TryGetValue(item.DeviceId, out var peer)
                ? peer
                : null;

        /// <summary>按设备类型选择发送实现（LocalSend 走 HTTP，自有协议走 TCP）。</summary>
        private async Task<bool> SendSingleToTargetAsync(
            string filePath,
            string? relativePath,
            TransferQueueControl? queue,
            Action<ulong, ulong>? progress)
        {
            var peer = SelectedLocalSendPeer;
            if (peer is not null && _localSendHost is not null)
            {
                var result = await _localSendHost
                    .SendFileAsync(peer, filePath, relativePath, progress, queue?.Token ?? CancellationToken.None)
                    .ConfigureAwait(true);

                if (!result.Success && !string.IsNullOrEmpty(result.Error))
                {
                    StatusMessage = Loc.F("发送失败: {0}", result.Error!);
                }

                return result.Success;
            }

            var target = SelectedDevice;
            if (target is null)
            {
                return false;
            }

            return await _serviceManager.SendSingleAsync(filePath, target, relativePath, queue).ConfigureAwait(true);
        }

        /// <summary>
        /// 语言切换后立即生效：重算依赖 Loc 的属性，并重建当前视图（{loc:Tr} 在视图加载时求值）。
        /// </summary>
        private void OnLanguageChanged()
        {
            RunOnUiThread(() =>
            {
                foreach (var device in Devices)
                {
                    device.RefreshDisplay();
                }

                foreach (var task in TransferTasks)
                {
                    task.RefreshLocalization();
                }

                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(DeviceCountText));
                OnPropertyChanged(nameof(QueueCountText));

                if (LocalDevice is not null)
                {
                    LocalDeviceSummary = Loc.F("本机：{0} · {1}:{2}", LocalDevice.DeviceName, LocalDevice.IpAddress, LocalDevice.Port);
                }

                // 清空再设回，强制 ViewLocator 重新创建视图，从而重新求值所有 {loc:Tr}
                var previous = CurrentView;
                CurrentView = null;
                CurrentView = previous is SettingsViewModel ? this : previous ?? this;

                StatusMessage = Loc.Get("语言已切换");
            });
        }

        /// <summary>
        /// 设置变化时刷新设备列表中的收藏状态：
        /// 这样在「接收请求」弹窗里勾选「以后自动接收」，列表里的 ★ 会立即同步（并置顶）。
        /// </summary>
        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            RunOnUiThread(() =>
            {
                foreach (var item in Devices)
                {
                    var isFavorite = _settingsService.Current.IsFavorite(item.DeviceId);
                    if (item.IsFavorite != isFavorite)
                    {
                        item.IsFavorite = isFavorite;
                    }
                }

                // 收藏设备置顶（Move 而不是清空重建，避免丢失 ListBox 选中项）
                var insertAt = 0;
                foreach (var favorite in Devices.Where(device => device.IsFavorite).ToList())
                {
                    var index = Devices.IndexOf(favorite);
                    if (index > insertAt)
                    {
                        Devices.Move(index, insertAt);
                    }

                    insertAt++;
                }
            });
        }
        /// <summary>把主窗口唤到前台（通知卡片点击、托盘恢复共用）。</summary>
        private void ActivateMainWindow()
        {
            var window = _appLifetime?.MainWindow;
            if (window is null)
            {
                return;
            }

            window.Show();
            if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                window.WindowState = Avalonia.Controls.WindowState.Normal;
            }

            window.Activate();
        }
        /// <summary>
        /// 首次信任某设备证书（TOFU）。
        /// </summary>
        private void OnFirstUseTrusted(string peerId, string fingerprint)
        {
            RunOnUiThread(() =>
            {
                StatusMessage = Loc.F("已记录新设备的证书指纹（{0}），再次连接会校验一致性", ShortenFingerprint(fingerprint));
            });

            _logger.LogInformation("TOFU 首次信任: {Peer} {Fingerprint}", peerId, fingerprint);
        }

        /// <summary>
        /// 证书指纹不一致：疑似中间人攻击。连接已被拒绝，这里只负责提醒用户。
        /// </summary>
        private void OnFingerprintMismatch(string peerId, string knownFingerprint, string presentedFingerprint)
        {
            _logger.LogWarning("证书指纹不一致（疑似中间人）: {Peer} 已知={Known} 本次={Presented}", peerId, knownFingerprint, presentedFingerprint);

            RunOnUiThread(() =>
            {
                StatusMessage = Loc.Get("安全警告：设备证书指纹与记录不一致，连接已拒绝");
                _ = _dialogService.ShowInfomationDialogAsync(
                    Loc.Get("安全警告"),
                    Loc.F("设备 {0} 的证书指纹与之前记录的不一致，可能存在中间人攻击，连接已被拒绝。\n\n", peerId) +
                    Loc.F("已记录：{0}\n", ShortenFingerprint(knownFingerprint)) +
                    Loc.F("本次：  {0}\n\n", ShortenFingerprint(presentedFingerprint)) +
                    Loc.Get("如果确认对方重装过系统/重新生成了证书，可在「设置 → 信任设备」中清除该设备的记录后重试。"));
            });
        }

        private static string ShortenFingerprint(string? fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return Loc.Get("(空)");
            }

            return fingerprint.Length <= 16 ? fingerprint : $"{fingerprint[..16]}…";
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

                StatusMessage = Loc.F("正在传输: {0} ({1:F1}%)", updatedInfo.FileName, updatedInfo.ProgressPercentage);
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
                        StatusMessage = Loc.F("文件传输完成: {0}", updatedInfo.FileName);
                        if (_appLifetime?.MainWindow is { IsActive: false })
                        {
                            _toastService?.Show(Loc.Get("传输完成"), updatedInfo.FileName, ActivateMainWindow);
                        }
                        _logger.LogInformation("文件传输完成: {File} ({Id})", updatedInfo.FileName, updatedInfo.TransferId);
                        break;
                    case TransferStatus.Failed:
                        StatusMessage = Loc.F("文件传输失败: {0}", errorMessage ?? string.Empty);
                        _logger.LogError("文件传输失败: {File} ({Id}), {Error}", updatedInfo.FileName, updatedInfo.TransferId, errorMessage);
                        break;
                    case TransferStatus.Cancelled:
                        StatusMessage = Loc.F("文件传输取消: {0}", errorMessage ?? string.Empty);
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
            _settingsService.Changed -= OnSettingsChanged;
            Loc.LanguageChanged -= OnLanguageChanged;
            if (_localSendHost is not null)
            {
                _localSendHost.PeerDiscovered -= OnLocalSendPeerDiscovered;
            }
            _serviceManager.OnFirstUseTrusted -= OnFirstUseTrusted;
            _serviceManager.OnFingerprintMismatch -= OnFingerprintMismatch;
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

        public IReadOnlyList<AppSkin> AvailableSkins { get; } = new[] { AppSkin.Win7, AppSkin.WinXP, AppSkin.WinXPOlive };

        public void Apply(AppSkin skin) => Current = skin;
    }
}
