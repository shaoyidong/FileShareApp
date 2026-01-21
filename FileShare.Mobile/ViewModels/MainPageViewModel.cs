using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Mobile.Services;
using Microsoft.Maui.Storage;
using System.Collections.ObjectModel;
using System.IO;
using System.Timers;
using System.Windows.Input;

namespace FileShare.Mobile.ViewModels;

public partial class MainPageViewModel : ViewModelBase
{
    private readonly FileShareServiceManager _serviceManager;
    private readonly System.Timers.Timer _timerCheckDeviceOnline;
    private readonly SynchronizationContext _uiContext;
    private readonly IAlertService _alertService;
    private string _localDeviceId;   

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

    public ObservableCollection<FileShare.Core.Models.DeviceInfo> Devices { get; }
    public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
    public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
    public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }
    
    public MainPageViewModel(IPlatformDirectoryService directoryService, IAlertService alertService)
    {
        _uiContext = SynchronizationContext.Current?? new SynchronizationContext();
        _alertService = alertService;
        Devices = new ObservableCollection<FileShare.Core.Models.DeviceInfo>();
        TransferTasks = new ObservableCollection<FileTransferViewModel>();
        SentTransferTasks = new ObservableCollection<FileTransferViewModel>();
        ReceivedTransferTasks = new ObservableCollection<FileTransferViewModel>();

        // 初始化服务管理器
        _serviceManager = new FileShareServiceManager(
            directoryService,
            Microsoft.Maui.Devices.DeviceInfo.Name,
            FileShare.Core.Models.DeviceType.Mobile);
        
        // 保存本地设备ID
        _localDeviceId = _serviceManager.GetLocalDeviceInfo().DeviceId;
        
        // 注册事件处理
        _serviceManager.OnDeviceDiscovered += OnDeviceDiscovered;
        _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestSendAndReceive;
        _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
        _serviceManager.OnTransferCompleted += OnTransferCompleted;

        // 初始化命令
        RefreshDevicesCommand = new RelayCommand(async () => RefreshDevicesAsync());
        SendFileCommand = new RelayCommand(async () => SendFileAsync());

        // 初始化设备在线检测定时器
        _timerCheckDeviceOnline = new System.Timers.Timer(5000);
        _timerCheckDeviceOnline.Elapsed += Timer_Elapsed;
        
        // 启动服务
        _ = InitializeAsync();       
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
            _timerCheckDeviceOnline.Start();
            StatusMessage = "服务已启动";
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动服务失败: {ex.Message}";
        }
    }    
   
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
            await _alertService.DisplayAlertAsync("提示", "请先选择目标设备", "确定");
            return;
        }
        
        try
        {
            // 打开文件选择器
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "选择要发送的文件"
            });
            
            if (result != null)
            {
                StatusMessage = $"正在发送文件: {result.FileName}";
                var success = await _serviceManager.SendFileAsync(result.FullPath, SelectedDevice);
                
                if (success)
                {
                    StatusMessage = "文件发送成功";
                    await _alertService.DisplayAlertAsync("成功", "文件发送成功", "确定");
                }
                else
                {
                    StatusMessage = "文件发送失败";
                    await _alertService.DisplayAlertAsync("失败", "文件发送失败", "确定");
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文件失败: {ex.Message}";
            await _alertService.DisplayAlertAsync("错误", ex.Message, "确定");
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
            await _alertService.DisplayAlertAsync("错误", ex.Message, "确定");
        }
    }
    
    [RelayCommand]
    private void RejectTransfer(FileTransferViewModel viewModel)
    {
        if (viewModel?.TransferId == null)
        {
            return;
        }

        _serviceManager.HandleTransferRequest(viewModel.TransferId, false);
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
    
    private void OnTransferRequestSendAndReceive(FileTransferInfo info)
    {
        _uiContext.Post(async (o) =>
        {
            // 创建FileTransferViewModel实例
            var transferViewModel = FileTransferViewModel.FromModel(info);
            
            // 添加到总任务列表
            TransferTasks.Add(transferViewModel);
            
            // 根据发送者ID判断是发送任务还是接收任务
            if (info.SenderId == _localDeviceId)
            {
                // 发送任务
                SentTransferTasks.Add(transferViewModel);
                StatusMessage = $"正在发送文件: {info.FileName}";
            }
            else
            {
                // 接收任务
                ReceivedTransferTasks.Add(transferViewModel);
                StatusMessage = $"收到文件传输请求: {info.FileName}";
                
                var senderDevice = Devices.FirstOrDefault(d => d.DeviceId == info.SenderId);
                if (senderDevice == null) 
                {
                    return;
                }
                transferViewModel.DeviceName = senderDevice.DeviceName;
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
    } 
    
    public void Dispose()
    {
        _serviceManager.Dispose();
        _timerCheckDeviceOnline?.Stop();
        _timerCheckDeviceOnline?.Dispose();
    }
}