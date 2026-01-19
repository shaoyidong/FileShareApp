using Moq;
using Xunit;
using FileShare.Desktop.ViewModels;
using FileShare.Core.Services;
using FileShare.Core.Models;
using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.Generic;
using System.Threading;
using FileShare.Desktop.Services;
using Avalonia.Platform.Storage;

namespace FileShare.Desktop.Tests.ViewModels
{
    public class MainWindowViewModelTests
    {
        private readonly Mock<IFileShareServiceManager> _mockServiceManager;
        private readonly Mock<IClassicDesktopStyleApplicationLifetime> _mockAppLifetime;
        private readonly Mock<IDialogService> _mockDialogService;
        private readonly MainWindowViewModel _viewModel;
        private readonly SynchronizationContext _testSynchronizationContext;

        public MainWindowViewModelTests()
        {
            // 设置测试同步上下文
            _testSynchronizationContext = new SynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_testSynchronizationContext);

            // 创建模拟对象
            _mockServiceManager = new Mock<IFileShareServiceManager>();
            _mockAppLifetime = new Mock<IClassicDesktopStyleApplicationLifetime>();
            _mockDialogService = new Mock<IDialogService>();

            // 设置模拟对象的行为
            _mockServiceManager.Setup(m => m.GetLocalDeviceInfo())
                .Returns(new DeviceInfo
                {
                    DeviceId = "test-device-id",
                    DeviceName = "Test Device",
                    DeviceType = DeviceType.Desktop,
                    Port = 5237
                });

            // 创建ViewModel实例
            _viewModel = new MainWindowViewModel(
                _mockServiceManager.Object,
                _mockDialogService.Object,
                _mockAppLifetime.Object,
                _testSynchronizationContext);
        }

        [Fact]
        public void Constructor_Initializes_Properties()
        {
            // Arrange & Act - 已在构造函数中完成

            // Assert
            Assert.NotNull(_viewModel.Devices);
            Assert.NotNull(_viewModel.TransferTasks);
            Assert.NotNull(_viewModel.SentTransferTasks);
            Assert.NotNull(_viewModel.ReceivedTransferTasks);
            Assert.NotNull(_viewModel.RefreshDevicesCommand);
            Assert.NotNull(_viewModel.SendFileCommand);
            Assert.NotNull(_viewModel.AcceptTransferCommand);
            Assert.NotNull(_viewModel.RejectTransferCommand);
            Assert.NotNull(_viewModel.RemoveTransferCommand);
            Assert.Equal("准备就绪", _viewModel.StatusMessage);
            Assert.False(_viewModel.IsScanning);
        }

        [Fact]
        public async Task RefreshDevicesAsync_Updates_StatusMessage()
        {
            // Arrange
            var initialStatus = _viewModel.StatusMessage;

            // Act
            _viewModel.RefreshDevicesCommand.Execute(null);

            // Assert
            Assert.NotEqual(initialStatus, _viewModel.StatusMessage);
            _mockServiceManager.Verify(m => m.RefreshDevices(), Times.Once);
        }

        [Fact]
        public void OnDeviceDiscovered_Add_Remote_Device()
        {
            // Arrange
            var localDevice = _mockServiceManager.Object.GetLocalDeviceInfo();
            var remoteDevice =
                new DeviceInfo
                {
                    DeviceId = "remote-device-1",
                    DeviceName = "Remote Device 1",
                    DeviceType = DeviceType.Desktop,
                    Port = 5237
                };

            // Act
            _mockServiceManager.Raise(m => m.OnDeviceDiscovered += null, remoteDevice);

            // Assert
            Assert.Contains(_viewModel.Devices, d => d.DeviceId == "remote-device-1");
        }

        [Fact]
        public void OnTransferRequestSendAndReceive_Adds_Transfer_Task()
        {
            // Arrange
            var transferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 0,
                Status = TransferStatus.Pending,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            // Act
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, transferInfo);

            // Assert
            Assert.Single(_viewModel.TransferTasks);
            Assert.Single(_viewModel.SentTransferTasks);
            Assert.Empty(_viewModel.ReceivedTransferTasks);
            Assert.Equal("test-transfer-1", _viewModel.TransferTasks[0].TransferId);
        }

        [Fact]
        public void OnTransferProgressUpdated_Updates_Transfer_Task()
        {
            // Arrange
            var initialTransferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 0,
                Status = TransferStatus.Pending,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            var updatedTransferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 512,
                Status = TransferStatus.Transferring,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            // Act - 先添加传输任务
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, initialTransferInfo);
            // 然后更新进度
            _mockServiceManager.Raise(m => m.OnTransferProgressUpdated += null, updatedTransferInfo);

            // Assert
            Assert.Single(_viewModel.TransferTasks);
            var transferTask = _viewModel.TransferTasks[0];
            Assert.Equal(TransferStatus.Transferring, transferTask.Status);
            Assert.Equal(512, transferTask.TransferredSize);
            Assert.Equal(50, transferTask.ProgressPercentage);
        }

        [Fact]
        public void OnTransferCompleted_Updates_Transfer_Task_Status()
        {
            // Arrange
            var initialTransferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 0,
                Status = TransferStatus.Pending,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            var completedTransferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 1024,
                Status = TransferStatus.Completed,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            // Act - 先添加传输任务
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, initialTransferInfo);
            // 然后完成传输
            _mockServiceManager.Raise(m => m.OnTransferCompleted += null, completedTransferInfo, null);

            // Assert
            Assert.Single(_viewModel.TransferTasks);
            var transferTask = _viewModel.TransferTasks[0];
            Assert.Equal(TransferStatus.Completed, transferTask.Status);
            Assert.Equal(1024, transferTask.TransferredSize);
            Assert.Equal(100, transferTask.ProgressPercentage);
        }

        [Fact]
        public void RejectTransfer_Calls_ServiceManager_HandleTransferRequest()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Pending
            };

            // Act
            _viewModel.RejectTransferCommand.Execute(transferViewModel);

            // Assert
            _mockServiceManager.Verify(m => m.HandleTransferRequest("test-transfer-1", false, null), Times.Once);
        }

        [Fact]
        public void RemoveTransfer_For_Pending_Status_Cancels_Transfer()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Pending,
                SenderId = "test-device-id"
            };

            // 添加到任务列表
            _viewModel.TransferTasks.Add(transferViewModel);
            _viewModel.SentTransferTasks.Add(transferViewModel);

            // Act
            _viewModel.RemoveTransferCommand.Execute(transferViewModel);

            // Assert
            _mockServiceManager.Verify(m => m.CancelTransfer("test-transfer-1"), Times.Once);
            Assert.Empty(_viewModel.TransferTasks);
            Assert.Empty(_viewModel.SentTransferTasks);
        }

        [Fact]
        public async Task SendFileCommand_No_Device_Selected_Shows_Error_Message()
        {
            // Arrange
            _viewModel.SelectedDevice = null;
            var initialStatus = _viewModel.StatusMessage;

            // Act
            _viewModel.SendFileCommand.Execute(null);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            Assert.Equal("请先选择目标设备", _viewModel.StatusMessage);
            Assert.NotEqual(initialStatus, _viewModel.StatusMessage);
        }

        [Fact]
        public async Task AcceptTransferCommand_Selects_Folder_And_Accepts_Transfer()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Pending
            };

            // 模拟文件夹选择对话框返回一个文件夹
            var mockFolder = new Mock<IStorageFolder>();
            var folderPath = "C:\\test\\folder";
            var folderUri = new Uri(folderPath);
            mockFolder.Setup(f => f.Path).Returns(folderUri);
            mockFolder.Setup(f => f.Name).Returns("folder");
            
            _mockDialogService.Setup(d => d.OpenFolderPickerAsync(It.IsAny<Avalonia.Platform.Storage.FolderPickerOpenOptions>()))
                .ReturnsAsync(new List<IStorageFolder> { mockFolder.Object });

            // Act
            _viewModel.AcceptTransferCommand.Execute(transferViewModel);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            _mockServiceManager.Verify(m => m.HandleTransferRequest("test-transfer-1", true, folderPath), Times.Once);
            Assert.Equal($"开始接收文件: {transferViewModel.FileName} (保存到: {folderPath})", _viewModel.StatusMessage);
        }

        [Fact]
        public async Task RemoveTransfer_For_Transferring_Status_User_Confirms_Cancels_Transfer()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Transferring,
                SenderId = "test-device-id"
            };

            // 添加到任务列表
            _viewModel.TransferTasks.Add(transferViewModel);
            _viewModel.SentTransferTasks.Add(transferViewModel);

            // 模拟对话框返回true（用户确认移除）
            _mockDialogService.Setup(d => d.ShowConfirmationDialogAsync("移除确认", "正在传输中，确定要移除吗？"))
                .ReturnsAsync(true);

            // Act
            _viewModel.RemoveTransferCommand.Execute(transferViewModel);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            _mockServiceManager.Verify(m => m.CancelTransfer("test-transfer-1"), Times.Once);
            Assert.Empty(_viewModel.TransferTasks);
            Assert.Empty(_viewModel.SentTransferTasks);
        }

        [Fact]
        public async Task RemoveTransfer_For_Completed_Status_Removes_From_List()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Completed,
                SenderId = "test-device-id"
            };

            // 添加到任务列表
            _viewModel.TransferTasks.Add(transferViewModel);
            _viewModel.SentTransferTasks.Add(transferViewModel);

            // Act
            _viewModel.RemoveTransferCommand.Execute(transferViewModel);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            // 对于已完成的任务，不应该调用CancelTransfer
            _mockServiceManager.Verify(m => m.CancelTransfer(It.IsAny<string>()), Times.Never);
            Assert.Empty(_viewModel.TransferTasks);
            Assert.Empty(_viewModel.SentTransferTasks);
        }

        [Fact]
        public async Task RemoveTransfer_For_Failed_Status_Removes_From_List()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Failed,
                SenderId = "test-device-id"
            };

            // 添加到任务列表
            _viewModel.TransferTasks.Add(transferViewModel);
            _viewModel.SentTransferTasks.Add(transferViewModel);

            // Act
            _viewModel.RemoveTransferCommand.Execute(transferViewModel);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            // 对于失败的任务，不应该调用CancelTransfer
            _mockServiceManager.Verify(m => m.CancelTransfer(It.IsAny<string>()), Times.Never);
            Assert.Empty(_viewModel.TransferTasks);
            Assert.Empty(_viewModel.SentTransferTasks);
        }

        [Fact]
        public async Task SendFileCommand_With_Device_Selected_Sends_File()
        {
            // Arrange
            var selectedDevice = new DeviceInfo
            {
                DeviceId = "remote-device-1",
                DeviceName = "Remote Device",
                DeviceType = DeviceType.Desktop,
                Port = 5237
            };
            _viewModel.SelectedDevice = selectedDevice;

            // 模拟文件选择对话框返回一个文件
            var mockFile = new Mock<IStorageFile>();
            var filePath = "C:\\test\\file.txt";
            var fileUri = new Uri(filePath);
            mockFile.Setup(f => f.Path).Returns(fileUri);
            mockFile.Setup(f => f.Name).Returns("file.txt");
            
            _mockDialogService.Setup(d => d.OpenFilePickerAsync(It.IsAny<Avalonia.Platform.Storage.FilePickerOpenOptions>()))
                .ReturnsAsync(new List<IStorageFile> { mockFile.Object });

            // 模拟发送文件成功
            _mockServiceManager.Setup(m => m.SendFileAsync(filePath, selectedDevice))
                .ReturnsAsync(true);

            // Act
            _viewModel.SendFileCommand.Execute(null);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            _mockServiceManager.Verify(m => m.SendFileAsync(filePath, selectedDevice), Times.Once);
            Assert.Equal("文件发送成功", _viewModel.StatusMessage);
        }

        [Fact]
        public void OnTransferCompleted_Updates_StatusMessage()
        {
            // Arrange
            var transferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 1024,
                Status = TransferStatus.Completed,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            // 先添加传输任务
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, transferInfo);

            // Act
            _mockServiceManager.Raise(m => m.OnTransferCompleted += null, transferInfo, null);

            // Assert
            Assert.Equal("文件传输完成: test.txt", _viewModel.StatusMessage);
        }

        [Fact]
        public void OnTransferCompleted_With_Error_Updates_StatusMessage()
        {
            // Arrange
            var transferInfo = new FileTransferInfo
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                FileSize = 1024,
                TransferredSize = 512,
                Status = TransferStatus.Failed,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
            };

            // 先添加传输任务
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, transferInfo);

            // Act
            _mockServiceManager.Raise(m => m.OnTransferCompleted += null, transferInfo, "网络错误");

            // Assert
            Assert.Equal("文件传输失败: 网络错误", _viewModel.StatusMessage);
        }
    }
}