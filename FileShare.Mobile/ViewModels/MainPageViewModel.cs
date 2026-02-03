using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Mobile.Messages;
using FileShare.Mobile.Services;
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
    private readonly INavigation _navigation;
    private readonly IAppManagementService _appManagementService;

    private string _statusMessage = "准备就绪";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
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
        set => SetProperty(ref _selectedDevice, value);
    }   

    public ICommand RefreshDevicesCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand NavigateToAppListCommand { get; }

    public ObservableCollection<FileShare.Core.Models.DeviceInfo> Devices { get; }
    public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
    public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
    public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }
    
    public MainPageViewModel(IPlatformDirectoryService directoryService
        , IFileShareServiceManager fileShareServiceManager
        , IFileTransferForegroundService fileTransferService
        , IAlertService alertService
        , IPickerService filePickerService
        , INavigation navigation
        , IAppManagementService appManagementService)
    {
        _uiContext = SynchronizationContext.Current?? new SynchronizationContext();
        _serviceManager = fileShareServiceManager;
        _foregroundService = fileTransferService;
        _alertService = alertService;
        _filePickerService = filePickerService;
        _navigation = navigation;
        _appManagementService = appManagementService;
        Devices = new ObservableCollection<FileShare.Core.Models.DeviceInfo>();
        TransferTasks = new ObservableCollection<FileTransferViewModel>();
        SentTransferTasks = new ObservableCollection<FileTransferViewModel>();
        ReceivedTransferTasks = new ObservableCollection<FileTransferViewModel>();

        // 保存本地设备ID
        _localDeviceId = _serviceManager.GetLocalDeviceInfo().DeviceId;
        
        // 注册事件处理
        _serviceManager.OnDeviceDiscovered += OnDeviceDiscovered;
        _serviceManager.OnDeviceRemoved += OnDeviceRemoved;
        _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestSendAndReceive;
        _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
        _serviceManager.OnTransferCompleted += OnTransferCompleted;

        // 订阅WeakReferenceMessenger消息，接收从AppListPage返回的APK路径
        WeakReferenceMessenger.Default.Register<AppSelectedMessage>(this, async (recipient, message) =>
        {
            await HandleAppSelected(message.Value);
        });

        // 初始化命令
        RefreshDevicesCommand = new RelayCommand(async () => RefreshDevicesAsync());
        SendFileCommand = new RelayCommand(async () => SendFileAsync());
        NavigateToAppListCommand = new RelayCommand(async () => await NavigateToAppList());
        
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
    
    private async Task InitializeAsync()
    {
        try
        {
            await _serviceManager.StartServicesAsync();
            StatusMessage = "准备就绪";
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动服务失败: {ex.Message}";
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
            _serviceManager.RefreshDevices();
            await Task.Delay(2000);
            StatusMessage = "扫描完成";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描设备失败: {ex.Message}";
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
            }          
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文件失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
        }
        finally
        {
            StopForegroundServiceIfNoTasksRunning();
        }
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
         
            _serviceManager.HandleTransferRequest(viewModel.TransferId, true);
            StatusMessage = $"开始接收文件: {viewModel.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"接受传输失败: {ex.Message}";
            await _alertService.DisplayToastAsync(ex.Message).ConfigureAwait(false);
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
            }
        },null);
    }
    
    private void OnDeviceRemoved(FileShare.Core.Models.DeviceInfo device)
    {
        // 更新设备列表，过滤掉本地设备
        var localDevice = _serviceManager.GetLocalDeviceInfo();
        if (device.DeviceId == localDevice.DeviceId)
        {
            return;
        }
        
        _uiContext.Post((o) =>
        {
            // 检查设备是否存在
            var existingDevice = Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            if (existingDevice != null)
            {
                Devices.Remove(existingDevice);
                StatusMessage = $"设备已离线: {device.DeviceName}";
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
            }
            else
            {
                // 接收任务
                ReceivedTransferTasks.Insert(0, transferViewModel);
                StatusMessage = $"收到文件传输请求: {info.FileName}";
                
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
        _uiContext.Send((o) =>
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
                        break;
                    case TransferStatus.Failed:
                        StatusMessage = $"文件传输失败: {errorMessage??string.Empty}";
                        break;
                    case TransferStatus.Cancelled:
                        StatusMessage = $"文件传输取消: {errorMessage ?? string.Empty}";
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
        }
    }
}