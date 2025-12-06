using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;

namespace FileShare.Desktop.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public string Greeting { get; } = "Welcome to Avalonia!";


        private readonly FileShareServiceManager _serviceManager;
        private readonly SynchronizationContext _uiContext;
        private readonly IClassicDesktopStyleApplicationLifetime _appLifetime;
        private readonly System.Timers.Timer _timerCheckDeviceOnline;

        private string _statusMessage = "准备就绪";
        private bool _isScanning;
        private DeviceInfo _selectedDevice;

        public string StatusMessage
        {
            get => _statusMessage;
            set => this.SetProperty(ref _statusMessage, value);
        }

        
        public bool IsScanning
        {
            get => _isScanning;
            set => this.SetProperty(ref _isScanning, value);
        }

        public DeviceInfo SelectedDevice
        {
            get => _selectedDevice;
            set => this.SetProperty(ref _selectedDevice, value);
        }

        public ObservableCollection<DeviceInfo> Devices { get; }
        public ObservableCollection<FileTransferInfo> TransferTasks { get; }

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand AcceptTransferCommand { get; }
        public ICommand RejectTransferCommand { get; }

        public MainWindowViewModel(IClassicDesktopStyleApplicationLifetime appLifetime)
        {
            _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
            _appLifetime = appLifetime;
            Devices = new ObservableCollection<DeviceInfo>();
            TransferTasks = new ObservableCollection<FileTransferInfo>();

            // 初始化服务管理器
            _serviceManager = new FileShareServiceManager(
                Environment.MachineName,
                DeviceType.Desktop);

            // 注册事件处理
            _serviceManager.OnDevicesUpdated += OnDevicesUpdated;
            _serviceManager.OnTransferRequestReceived += OnTransferRequestReceived;
            _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
            _serviceManager.OnTransferCompleted += OnTransferCompleted;

            // 初始化命令
            RefreshDevicesCommand = new RelayCommand(async () => await RefreshDevicesAsync());
            SendFileCommand = new RelayCommand(async () => await SendFileAsync());
            AcceptTransferCommand = new RelayCommand<FileTransferInfo>(AcceptTransfer);
            RejectTransferCommand = new RelayCommand<FileTransferInfo>(RejectTransfer);

            _timerCheckDeviceOnline = new System.Timers.Timer(5000);
            _timerCheckDeviceOnline.Elapsed += Timer_Elapsed;
            // 启动服务
            _ = InitializeAsync();
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            List<DeviceInfo> removeDevices = new List<DeviceInfo>();
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
                await Task.Delay(2000); // 等待设备响应
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
                StatusMessage = "请先选择目标设备";
                return;
            }

            // 打开文件选择对话框
            var topLevel = TopLevel.GetTopLevel(_appLifetime.MainWindow);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要发送的文件",
                AllowMultiple = false
            });

            if (files.Any())
            {
                var file = files[0];
                var filePath = file.Path.LocalPath;

                try
                {
                    StatusMessage = $"正在发送文件: {Path.GetFileName(filePath)}";
                    var success = await _serviceManager.SendFileAsync(filePath, SelectedDevice);

                    if (success)
                    {
                        StatusMessage = "文件发送成功";
                    }
                    else
                    {
                        StatusMessage = "文件发送失败";
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"发送文件失败: {ex.Message}";
                }
            }
        }

        private void AcceptTransfer(FileTransferInfo info)
        {
            _serviceManager.HandleTransferRequest(info, true);
            StatusMessage = $"开始接收文件: {info.FileName}";
        }

        private void RejectTransfer(FileTransferInfo info)
        {
            _serviceManager.HandleTransferRequest(info, false);
            StatusMessage = $"已拒绝文件: {info.FileName}";
        }

        private void OnDevicesUpdated(List<DeviceInfo> devices)
        {
            // 更新设备列表，过滤掉本地设备
            var localDevice = _serviceManager.GetLocalDeviceInfo();
            var remoteDevices = devices.Where(d => d.DeviceId != localDevice.DeviceId).ToList();

            _uiContext.Send(_=>
            {
                Devices.Clear();
                foreach (var device in remoteDevices)
                {
                    Devices.Add(device);
                }
            }, null);           

            StatusMessage = $"发现 {remoteDevices.Count} 台设备";
        }

        private void OnTransferRequestReceived(FileTransferInfo info)
        {
            TransferTasks.Add(info);
            StatusMessage = $"收到文件传输请求: {info.FileName}";
        }

        private void OnTransferProgressUpdated(string transferId, long transferredSize, long totalSize)
        {
            // 查找并更新传输任务
            var existingTask = TransferTasks.FirstOrDefault(t => t.TransferId == transferId);
            if (existingTask != null)
            {
                existingTask.Status = TransferStatus.Transferring;
                existingTask.TransferredSize = transferredSize;

                var progress = (double)transferredSize / totalSize * 100;
                StatusMessage = $"正在传输: {existingTask.FileName} ({progress:F1}%)";
            }
        }

        private void OnTransferCompleted(string transferId, bool success, string? errorMessage)
        {
            // 查找并更新传输任务
            var existingTask = TransferTasks.FirstOrDefault(t => t.TransferId == transferId);
            if (existingTask != null)
            {
                existingTask.Status = success ? TransferStatus.Completed : TransferStatus.Failed;
                StatusMessage = success ? $"文件传输完成: {existingTask.FileName}" : $"传输失败: {errorMessage}";
            }
        }

        public void Dispose()
        {
            _serviceManager.Dispose();
            _timerCheckDeviceOnline?.Stop();
            _timerCheckDeviceOnline?.Dispose();
        }

    }
}
