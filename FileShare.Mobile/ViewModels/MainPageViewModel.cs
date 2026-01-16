using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FileShare.Core.Models;
using FileShare.Core.Services;
using Microsoft.Maui.Storage;

namespace FileShare.Mobile.ViewModels;

public class MainPageViewModel : INotifyPropertyChanged
{
    private readonly FileShareServiceManager _serviceManager;
    private string _statusMessage = "准备就绪";
    private bool _isScanning;
    private FileShare.Core.Models.DeviceInfo _selectedDevice;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }
    
    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); }
    }
    
    public FileShare.Core.Models.DeviceInfo SelectedDevice
    {
        get => _selectedDevice;
        set { _selectedDevice = value; OnPropertyChanged(); }
    }
    
    public ObservableCollection<FileShare.Core.Models.DeviceInfo> Devices { get; }
    public ObservableCollection<FileTransferInfo> TransferTasks { get; }
    
    public ICommand RefreshDevicesCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand AcceptTransferCommand { get; }
    public ICommand RejectTransferCommand { get; }
    
    public MainPageViewModel()
    {
        Devices = new ObservableCollection<FileShare.Core.Models.DeviceInfo>();
        TransferTasks = new ObservableCollection<FileTransferInfo>();
        
        // 初始化服务管理器
        _serviceManager = new FileShareServiceManager(
            Microsoft.Maui.Devices.DeviceInfo.Name,
            FileShare.Core.Models.DeviceType.Mobile);
        
        // 注册事件处理
        _serviceManager.OnDevicesUpdated += OnDevicesUpdated;
        _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestReceived;
        _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
        //_serviceManager.OnTransferCompleted += OnTransferCompleted;
        
        // 初始化命令
        RefreshDevicesCommand = new Command(async () => await RefreshDevicesAsync());
        SendFileCommand = new Command(async () => await SendFileAsync());
        AcceptTransferCommand = new Command<FileTransferInfo>(AcceptTransfer);
        RejectTransferCommand = new Command<FileTransferInfo>(RejectTransfer);
        
        // 启动服务
        _ = InitializeAsync();
    }
    
    private async Task InitializeAsync()
    {
        try
        {
            await _serviceManager.StartServicesAsync();
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描设备失败: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            StatusMessage = "扫描完成";
        }
    }
    
    private async Task SendFileAsync()
    {
        if (SelectedDevice == null)
        {
            await Application.Current.MainPage.DisplayAlert("提示", "请先选择目标设备", "确定");
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
                    await Application.Current.MainPage.DisplayAlert("成功", "文件发送成功", "确定");
                }
                else
                {
                    StatusMessage = "文件发送失败";
                    await Application.Current.MainPage.DisplayAlert("失败", "文件发送失败", "确定");
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送文件失败: {ex.Message}";
            await Application.Current.MainPage.DisplayAlert("错误", ex.Message, "确定");
        }
    }
    
    private void AcceptTransfer(FileTransferInfo info)
    {
        _serviceManager.HandleTransferRequest(info.TransferId, true);
        StatusMessage = $"开始接收文件: {info.FileName}";
    }
    
    private void RejectTransfer(FileTransferInfo info)
    {
        _serviceManager.HandleTransferRequest(info.TransferId, false);
        StatusMessage = $"已拒绝文件: {info.FileName}";
    }
    
    private void OnDevicesUpdated(List<FileShare.Core.Models.DeviceInfo> devices)
    {
        // 更新设备列表
        var localDevice = _serviceManager.GetLocalDeviceInfo();
        var remoteDevices = devices.Where(d => d.DeviceId != localDevice.DeviceId).ToList();
        
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Devices.Clear();
            foreach (var device in remoteDevices)
            {
                Devices.Add(device);
            }
            
            StatusMessage = $"发现 {remoteDevices.Count} 台设备";
        });
    }
    
    private void OnTransferRequestReceived(FileTransferInfo info)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            TransferTasks.Add(info);
            StatusMessage = $"收到文件传输请求: {info.FileName}";
            
            // 显示确认对话框
            var result = await Application.Current.MainPage.DisplayAlert(
                "文件传输请求",
                $"来自 {info.SenderId} 的文件: {info.FileName} ({FormatFileSize(info.FileSize)})",
                "接受", "拒绝");
            
            if (result)
            {
                AcceptTransfer(info);
            }
            else
            {
                RejectTransfer(info);
            }
        });
    }
    
    private void OnTransferProgressUpdated(FileTransferInfo updatedInfo)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 查找并替换传输任务，实现GUI刷新
            var index = TransferTasks.IndexOf(TransferTasks.FirstOrDefault(t => t.TransferId == updatedInfo.TransferId));
            if (index >= 0)
            {
                TransferTasks[index] = updatedInfo;
                
                var progress = (double)updatedInfo.TransferredSize / updatedInfo.FileSize * 100;
                StatusMessage = $"正在传输: {updatedInfo.FileName} ({progress:F1}%)";
            }
        });
    }
    
    private void OnTransferCompleted(FileTransferInfo updatedInfo, bool success, string? errorMessage)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // 查找并替换传输任务，实现GUI刷新
            var index = TransferTasks.IndexOf(TransferTasks.FirstOrDefault(t => t.TransferId == updatedInfo.TransferId));
            if (index >= 0)
            {
                TransferTasks[index] = updatedInfo;
                StatusMessage = success ? $"文件传输完成: {updatedInfo.FileName}" : $"传输失败: {errorMessage ?? "未知错误"}";
            }
        });
    }
    
    private string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        else if (bytes < 1024 * 1024)
            return $"{(bytes / 1024.0):F2} KB";
        else if (bytes < 1024 * 1024 * 1024)
            return $"{(bytes / (1024.0 * 1024)):F2} MB";
        else
            return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
    }
    
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    public void Dispose()
    {
        _serviceManager.Dispose();
    }
}