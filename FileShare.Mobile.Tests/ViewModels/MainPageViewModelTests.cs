using Moq;
using Xunit;
using FileShare.Mobile.ViewModels;
using FileShare.Core.Services;
using FileShare.Core.Models;
using FileShare.Mobile.Services;
using Microsoft.Maui.Storage;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Mobile.Tests.ViewModels
{
    public class MainPageViewModelTests
    {
        private readonly Mock<IFileShareServiceManager> _mockServiceManager;
    private readonly Mock<IAlertService> _mockAlertService;
    private readonly Mock<IFileTransferForegroundService> _mockForegroundService;
    private readonly Mock<IPlatformDirectoryService> _mockDirectoryService;
    private readonly Mock<IPickerService> _mockFilePickerService;
    private readonly MainPageViewModel _viewModel;
    private readonly SynchronizationContext _testSynchronizationContext;

        public MainPageViewModelTests()
        {
            // 设置测试同步上下文
            _testSynchronizationContext = SynchronizationContext.Current ?? new SynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_testSynchronizationContext);

            // 创建模拟对象
            _mockServiceManager = new Mock<IFileShareServiceManager>();
            _mockAlertService = new Mock<IAlertService>();
            _mockForegroundService = new Mock<IFileTransferForegroundService>();
            _mockDirectoryService = new Mock<IPlatformDirectoryService>();
            _mockFilePickerService = new Mock<IPickerService>();

            // 设置模拟对象的行为
            _mockServiceManager.Setup(m => m.GetLocalDeviceInfo())
                .Returns(new Core.Models.DeviceInfo
                {
                    DeviceId = "test-device-id",
                    DeviceName = "Test Device",
                    DeviceType = Core.Models.DeviceType.Mobile,
                    Port = 5237,
                    IpAddress = "127.0.0.1"
                });

            // 模拟前台服务方法
            _mockForegroundService.Setup(f => f.StartServiceAsync())
                .Returns(Task.CompletedTask);
            _mockForegroundService.Setup(f => f.StopService())
                .Verifiable();

            // 模拟服务启动
            _mockServiceManager.Setup(m => m.StartServicesAsync())
                .Returns(Task.CompletedTask);

            // 创建ViewModel实例
            _viewModel = new MainPageViewModel(
                _mockDirectoryService.Object,
                _mockServiceManager.Object,
                _mockForegroundService.Object,
                _mockAlertService.Object,
                _mockFilePickerService.Object);
        }

        [Fact]
        public void Constructor_Initializes_Properties()
        {
            // Assert
            Assert.NotNull(_viewModel.Devices);
            Assert.NotNull(_viewModel.TransferTasks);
            Assert.NotNull(_viewModel.SentTransferTasks);
            Assert.NotNull(_viewModel.ReceivedTransferTasks);
            Assert.NotNull(_viewModel.RefreshDevicesCommand);
            Assert.NotNull(_viewModel.SendFileCommand);
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

            // 等待异步操作完成
            await Task.Delay(3000);

            // Assert
            Assert.NotEqual(initialStatus, _viewModel.StatusMessage);
            Assert.Equal("扫描完成", _viewModel.StatusMessage);
            _mockServiceManager.Verify(m => m.RefreshDevices(), Times.Once);
        }

        [Fact]
        public async Task SendFileAsync_No_Device_Selected_Shows_Error_Message()
        {
            // Arrange
            _viewModel.SelectedDevice = null;
            var initialStatus = _viewModel.StatusMessage;

            // Act
            _viewModel.SendFileCommand.Execute(null);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            _mockAlertService.Verify(a => a.DisplayToastAsync("请先选择目标设备"), Times.Once);
        }

        [Fact]
        public void OnDeviceDiscovered_Adds_Remote_Device()
        {
            // Arrange
            var remoteDevice = new Core.Models.DeviceInfo
            {
                DeviceId = "remote-device-1",
                DeviceName = "Remote Device 1",
                DeviceType = Core.Models.DeviceType.Mobile,
                Port = 5237,
                IpAddress = "192.168.1.100",
                LastSeen = DateTime.Now
            };

            // Act
            _mockServiceManager.Raise(m => m.OnDeviceDiscovered += null, remoteDevice);

            // 等待UI线程处理
            Thread.Sleep(100);

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

            // 等待UI线程处理
            Thread.Sleep(100);

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
            // 等待UI线程处理
            Thread.Sleep(100);
            // 然后更新进度
            _mockServiceManager.Raise(m => m.OnTransferProgressUpdated += null, updatedTransferInfo);
            // 等待UI线程处理
            Thread.Sleep(100);

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
            // 等待UI线程处理
            Thread.Sleep(100);
            // 然后完成传输
            _mockServiceManager.Raise(m => m.OnTransferCompleted += null, completedTransferInfo, null);
            // 等待UI线程处理
            Thread.Sleep(100);

            // Assert
            Assert.Single(_viewModel.TransferTasks);
            var transferTask = _viewModel.TransferTasks[0];
            Assert.Equal(TransferStatus.Completed, transferTask.Status);
            Assert.Equal(1024, transferTask.TransferredSize);
            Assert.Equal(100, transferTask.ProgressPercentage);
            Assert.Equal("文件传输完成: test.txt", _viewModel.StatusMessage);
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
            _mockServiceManager.Verify(m => m.HandleTransferRequest("test-transfer-1", false), Times.Once);
        }

        [Fact]
        public async Task RemoveTransfer_For_Pending_Status_Cancels_Transfer()
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

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            _mockServiceManager.Verify(m => m.CancelTransfer("test-transfer-1"), Times.Once);
            Assert.Empty(_viewModel.TransferTasks);
            Assert.Empty(_viewModel.SentTransferTasks);
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
            _mockAlertService.Setup(a => a.DisplayAlertAsync("确认移除", "正在传输中，确定要移除吗？", "确定", "取消"))
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
        public void AcceptTransfer_Calls_ServiceManager_HandleTransferRequest()
        {
            // Arrange
            var transferViewModel = new FileTransferViewModel
            {
                TransferId = "test-transfer-1",
                FileName = "test.txt",
                Status = TransferStatus.Pending
            };

            // Act
            _viewModel.AcceptTransferCommand.Execute(transferViewModel);

            // Assert
            _mockServiceManager.Verify(m => m.HandleTransferRequest("test-transfer-1", true), Times.Once);
        }

        [Fact]
        public void Dispose_Stops_Services()
        {
            // Act
            _viewModel.Dispose();

            // Assert
            _mockServiceManager.Verify(m => m.Dispose(), Times.Once);
            _mockForegroundService.Verify(f => f.StopService(), Times.Once);
        }

        [Fact]
        public async Task SendFileCommand_With_Device_Selected_Sends_File()
        {
            // Arrange
            var selectedDevice = new Core.Models.DeviceInfo
            {
                DeviceId = "remote-device-1",
                DeviceName = "Remote Device",
                DeviceType = Core.Models.DeviceType.Mobile,
                Port = 5237,
                IpAddress = "192.168.1.100"
            };
            _viewModel.SelectedDevice = selectedDevice;

            // 创建真实的 FileResult 实例
            var filePath = "C:\\test\\file.txt";

            // 方法1：如果 FileResult 有公共构造函数
            var fileResult = new FileResult(filePath);

            // 或者，如果 FileResult 没有公共构造函数，使用反射创建
            // var fileResult = CreateFileResult(filePath, "file.txt");
            // 模拟文件选择器返回文件
            //var mockFileResult = new Mock<FileResult>();
            //var filePath = "C:\\test\\file.txt";
            //mockFileResult.Setup(f => f.FullPath).Returns(filePath);
            //mockFileResult.Setup(f => f.FileName).Returns("file.txt");
            
            _mockFilePickerService.Setup(f => f.PickFileAsync(It.Is<PickOptions>(opt =>
                opt.PickerTitle == "选择要发送的文件")))
                .ReturnsAsync(fileResult);

            // 模拟前台服务启动
            _mockForegroundService.Setup(f => f.StartServiceAsync())
                .Returns(Task.CompletedTask);

            // 模拟文件发送成功
            _mockServiceManager.Setup(m => m.SendFileAsync(filePath, selectedDevice))
                .ReturnsAsync(true);

            // Act
            _viewModel.SendFileCommand.Execute(null);

            // 等待异步操作完成
            await Task.Delay(100);

            // Assert
            _mockForegroundService.Verify(f => f.StartServiceAsync(), Times.Once);
            _mockFilePickerService.Verify(f => f.PickFileAsync(It.Is<PickOptions>(opt =>
                opt.PickerTitle == "选择要发送的文件")), Times.Once);
            _mockServiceManager.Verify(m => m.SendFileAsync(filePath, selectedDevice), Times.Once);
        }

        [Fact]
        public async Task SendFileCommand_With_Device_Selected_But_No_File_Selected()
        {
            // Arrange
            var selectedDevice = new Core.Models.DeviceInfo
            {
                DeviceId = "remote-device-1",
                DeviceName = "Remote Device",
                DeviceType = Core.Models.DeviceType.Mobile,
                Port = 5237,
                IpAddress = "192.168.1.100"
            };
            _viewModel.SelectedDevice = selectedDevice;

            // 使用 It.Is<> 匹配参数
            _mockFilePickerService.Setup(f => f.PickFileAsync(It.Is<PickOptions>(opt =>
                opt.PickerTitle == "选择要发送的文件")))
                .ReturnsAsync((FileResult?)null);

            _mockForegroundService.Setup(f => f.StartServiceAsync())
                .Returns(Task.CompletedTask);
            _mockForegroundService.Setup(f => f.StopService())
                .Verifiable();

            // Act
            _viewModel.SendFileCommand.Execute(null);

            // 增加延迟确保异步操作完成
            await Task.Delay(500);

            // Assert
            _mockForegroundService.Verify(f => f.StartServiceAsync(), Times.Once);

            // 使用相同的参数匹配器
            _mockFilePickerService.Verify(f => f.PickFileAsync(It.Is<PickOptions>(opt =>
                opt.PickerTitle == "选择要发送的文件")), Times.Once);

            _mockForegroundService.Verify(f => f.StopService(), Times.Once);
        }
    }
}
