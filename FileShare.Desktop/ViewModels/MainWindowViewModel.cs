using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
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

        private readonly IFileShareServiceManager _serviceManager;
        private readonly IDialogService _dialogService;
        private readonly SynchronizationContext _uiContext;
        private readonly IClassicDesktopStyleApplicationLifetime? _appLifetime;

        private string _statusMessage = "准备就绪";
        private bool _isScanning;
        [ObservableProperty]
        private DeviceInfo? _selectedDevice;
        private string _localDeviceId;

        [ObservableProperty]
        private object _currentView;

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

        public ObservableCollection<DeviceInfo> Devices { get; }
        public ObservableCollection<FileTransferViewModel> TransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> SentTransferTasks { get; }
        public ObservableCollection<FileTransferViewModel> ReceivedTransferTasks { get; }

        public ICommand RefreshDevicesCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand AcceptTransferCommand { get; }
        public ICommand RejectTransferCommand { get; }
        public ICommand RemoveTransferCommand { get; }
        public ICommand ShowHistoryCommand { get; }

        /// <summary>
        /// 构造函数，用于依赖注入
        /// </summary>
        /// <param name="serviceManager">文件共享服务管理器</param>
        /// <param name="dialogService">对话框服务</param>
        /// <param name="appLifetime">应用程序生命周期</param>
        /// <param name="uiContext">UI上下文</param>
        public MainWindowViewModel(IFileShareServiceManager serviceManager, 
                                  IDialogService dialogService,                                 
                                  IClassicDesktopStyleApplicationLifetime appLifetime,
                                  SynchronizationContext uiContext)
        {
            _serviceManager = serviceManager;
            _dialogService = dialogService;
            _uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
            _appLifetime = appLifetime;
            Devices = new ObservableCollection<DeviceInfo>();
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

            // 初始化命令
            RefreshDevicesCommand = new RelayCommand(async () => RefreshDevicesAsync());
            SendFileCommand = new RelayCommand(async () => SendFileAsync());
            AcceptTransferCommand = new RelayCommand<FileTransferViewModel>(AcceptTransfer);
            RejectTransferCommand = new RelayCommand<FileTransferViewModel>(RejectTransfer);
            RemoveTransferCommand = new RelayCommand<FileTransferViewModel>(RemoveTransfer);
            ShowHistoryCommand = new RelayCommand(ShowHistory);

            // 初始化当前视图
            CurrentView = this;

            // 启动服务
            _ = InitializeAsync();
        }

        private void ShowHistory()
        {
            CurrentView = new HistoryViewModel(_serviceManager, _dialogService, () => 
            {
                CurrentView = this;
            });
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
                StatusMessage = "准备就绪";
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
                await _serviceManager.RefreshDevicesAsync();
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
            var files = await _dialogService.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
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

        private async void AcceptTransfer(FileTransferViewModel? viewModel)
        {
            if (viewModel?.TransferId == null)
            {
                return;
            }
            var folder = await _dialogService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择文件保存位置",
                AllowMultiple = false
            });

            string? savePath = null;
            if (folder.Count > 0)
            {
                savePath = folder[0].Path.LocalPath;
            }
            else
            {
                return;
            }

                _serviceManager.HandleTransferRequest(viewModel.TransferId, true, savePath);
            StatusMessage = savePath != null 
                ? $"开始接收文件: {viewModel.FileName} (保存到: {savePath})" 
                : $"开始接收文件: {viewModel.FileName}";
        }

        private void RejectTransfer(FileTransferViewModel? viewModel)
        {
            if (viewModel?.TransferId == null)
            {
                return;
            }
            _serviceManager.HandleTransferRequest(viewModel.TransferId, false);
            StatusMessage = $"已拒绝文件: {viewModel.FileName}";
        }   

        private void OnDeviceDiscovered(DeviceInfo device)
        {
            // 更新设备列表，过滤掉本地设备
            var localDevice = _serviceManager.GetLocalDeviceInfo();

            if (device.DeviceId == localDevice.DeviceId)
            {
                return;
            }

            _uiContext.Send(_ =>
            {
                Devices.Add(device);
            }, null);

            StatusMessage = $"发现设备: {device.DeviceName}";
        }
        
        private void OnDeviceRemoved(DeviceInfo device)
        {
            // 更新设备列表，过滤掉本地设备
            var localDevice = _serviceManager.GetLocalDeviceInfo();

            if (device.DeviceId == localDevice.DeviceId)
            {
                return;
            }

            _uiContext.Send(_ =>
            {
                var existingDevice = Devices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
                if (existingDevice != null)
                {
                    Devices.Remove(existingDevice);
                }
            }, null);

            StatusMessage = $"设备已离线: {device.DeviceName}";
        }

        private async void RemoveTransfer(FileTransferViewModel? model)
        {
            if (model?.TransferId == null)
                return;
            switch (model.Status)
            {
                case TransferStatus.Pending:
                    //var message = new ConfirmationMessage("正在传输中，确定要移除吗？", "确认移除", false);
                    //WeakReferenceMessenger.Default.Send(message);
                    //// 等待对话框结果
                    //var result = await message.CompletionSource.Task;
                    //if (!result)
                    //{
                    //    return;
                    //}                     
                    _serviceManager.CancelTransfer(model.TransferId);
                    StatusMessage = $"已拒绝文件: {model.FileName}";
                    break;
                case TransferStatus.Transferring:
                    // 使用对话框服务显示确认对话框
                    var result = await _dialogService.ShowConfirmationDialogAsync("移除确认", "正在传输中，确定要移除吗？");
                    if (!result)
                    {
                        return;
                    }
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
            TransferTasks.Insert(0, transferViewModel);
            
            // 根据发送者ID判断是发送任务还是接收任务
            if (info.SenderId == _localDeviceId)
            {
                // 发送任务
                SentTransferTasks.Insert(0,transferViewModel);
                StatusMessage = $"正在发送文件: {info.FileName}";
            }
            else
            {
                // 接收任务
                ReceivedTransferTasks.Insert(0,transferViewModel);
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
        }

    }
}
