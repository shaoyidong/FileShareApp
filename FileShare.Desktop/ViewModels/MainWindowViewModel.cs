using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.ViewModels.Messages;
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
        private string _localDeviceId;

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
        public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand AcceptTransferCommand { get; }
        public ICommand RejectTransferCommand { get; }
        public ICommand RemoveTransferCommand { get; }

        public MainWindowViewModel(IClassicDesktopStyleApplicationLifetime appLifetime)
        {
            _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
            _appLifetime = appLifetime;
            Devices = new ObservableCollection<DeviceInfo>();
            TransferTasks = new ObservableCollection<FileTransferViewModel>();
            SentTransferTasks = new ObservableCollection<FileTransferViewModel>();
            ReceivedTransferTasks = new ObservableCollection<FileTransferViewModel>();

            // 初始化服务管理器
            _serviceManager = new FileShareServiceManager(
                Environment.MachineName,
                DeviceType.Desktop);
                
            // 保存本地设备ID
            _localDeviceId = _serviceManager.GetLocalDeviceInfo().DeviceId;

            // 注册事件处理
            _serviceManager.OnDevicesUpdated += OnDevicesUpdated;
            _serviceManager.OnTransferRequestSendAndReceive += OnTransferRequestSendAndReceive;
            _serviceManager.OnTransferProgressUpdated += OnTransferProgressUpdated;
            _serviceManager.OnTransferCompleted += OnTransferCompleted;

            // 初始化命令
            RefreshDevicesCommand = new RelayCommand(async () => await RefreshDevicesAsync());
            SendFileCommand = new RelayCommand(async () => await SendFileAsync());
            AcceptTransferCommand = new RelayCommand<FileTransferViewModel>(AcceptTransfer);
            RejectTransferCommand = new RelayCommand<FileTransferViewModel>(RejectTransfer);
            RemoveTransferCommand = new RelayCommand<FileTransferViewModel>(RemoveTransfer);

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

        private async void AcceptTransfer(FileTransferViewModel viewModel)
        {
            var topLevel = TopLevel.GetTopLevel(_appLifetime.MainWindow);
            var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择文件保存位置",
                AllowMultiple = false
            });

            string? savePath = null;
            if (folder.Count > 0)
            {
                savePath = folder[0].Path.LocalPath;
            }

            _serviceManager.HandleTransferRequest(viewModel.TransferId, true, savePath);
            StatusMessage = savePath != null 
                ? $"开始接收文件: {viewModel.FileName} (保存到: {savePath})" 
                : $"开始接收文件: {viewModel.FileName}";
        }

        private void RejectTransfer(FileTransferViewModel viewModel)
        {
            _serviceManager.HandleTransferRequest(viewModel.TransferId, false);
            StatusMessage = $"已拒绝文件: {viewModel.FileName}";
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

        private async void RemoveTransfer(FileTransferViewModel? model)
        {
            if (model == null)
                return;
            switch (model.Status)
            {
                case TransferStatus.Pending:
                    var message = new ConfirmationMessage("正在传输中，确定要移除吗？", "确认移除", false);
                    WeakReferenceMessenger.Default.Send(message);
                    // 等待对话框结果
                    var result = await message.CompletionSource.Task;
                    if (!result)
                    {
                        return;
                    }
                    _serviceManager.CancelTransfer(model.TransferId);
                    StatusMessage = $"已拒绝文件: {model.FileName}";
                    break;
                case TransferStatus.Transferring:

                    //// 发送消息请求显示确认对话框
                    //var message = new ConfirmationMessage("正在传输中，确定要移除吗？", "确认移除", false);
                    //WeakReferenceMessenger.Default.Send(message);
                    //// 等待对话框结果
                    //var result = await message.CompletionSource.Task;
                    //if (!result)
                    //{
                    //    return;
                    //}
                    _serviceManager.CancelTransfer(model.TransferId);
                    break;
                case TransferStatus.Completed:
                case TransferStatus.Failed:
                case TransferStatus.Cancelled:
                    break;
                default:
                    break;
            }

            TransferTasks.Remove(model);
            if (model.SenderId == _localDeviceId)
            {
                SentTransferTasks.Remove(model);
            }
            else
            {
                ReceivedTransferTasks.Remove(model);
            }
        }

        private void OnTransferRequestSendAndReceive(FileTransferInfo info)
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
            }
        }

        

        private void OnTransferProgressUpdated(FileTransferInfo updatedInfo)
        {
            // 查找对应的FileTransferViewModel并更新
            // 由于所有任务列表中的任务都是同一个对象引用
            // 所以只需要更新TransferTasks列表中的任务即可，其他列表中的任务会自动更新
            var viewModel = TransferTasks.FirstOrDefault(t => t.TransferId == updatedInfo.TransferId);
            if (viewModel != null)
            {
                //_uiContext.Send(_ =>
                //{
                //    // 使用UpdateFrom方法更新属性，触发通知
                    
                //}, null);
                viewModel.Status = updatedInfo.Status;
                viewModel.FileSize = updatedInfo.FileSize;
                viewModel.TransferredSize = updatedInfo.TransferredSize;
                viewModel.ProgressPercentage= updatedInfo.ProgressPercentage;
               
                StatusMessage = $"正在传输: {updatedInfo.FileName} ({updatedInfo.ProgressPercentage:F1}%)";
            }
        }

        private void OnTransferCompleted(FileTransferInfo updatedInfo, string? errorMessage)
        {
            // 查找对应的FileTransferViewModel并更新
            // 由于所有任务列表中的任务都是同一个对象引用
            // 所以只需要更新TransferTasks列表中的任务即可，其他列表中的任务会自动更新
            var viewModel = TransferTasks.FirstOrDefault(t => t.TransferId == updatedInfo.TransferId);
            if (viewModel != null)
            {
                //_uiContext.Send(_ =>
                //{
                //    // 使用UpdateFrom方法更新属性，触发通知
                //    viewModel.UpdateFrom(updatedInfo);
                //}, null);

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
        }

        public void Dispose()
        {
            _serviceManager.Dispose();
            _timerCheckDeviceOnline?.Stop();
            _timerCheckDeviceOnline?.Dispose();
        }

    }
}
