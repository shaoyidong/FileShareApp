using Moq;
using Xunit;
using FileShare.Desktop.ViewModels;
using FileShare.Core.Services;
using FileShare.Core.Models;
using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.Generic;
using System.Threading;
using FileShare.Desktop.Services;
using CommunityToolkit.Mvvm.Messaging;
using Shyd.Prism.Dialogs;
using Shyd.Prism.Regions;
using Avalonia.Platform.Storage;

namespace FileShare.Desktop.Tests.ViewModels
{
    public class MainWindowViewModelTests
    {
        private readonly Mock<IFileShareServiceManager> _mockServiceManager;
        private readonly Mock<IClassicDesktopStyleApplicationLifetime> _mockAppLifetime;
        private readonly Mock<IPlatformDialogService> _mockDialogService;
        private readonly Mock<IDialogService> _mockDialogs;
        private readonly Mock<IRegionManager> _mockRegionManager;
        private readonly IMessenger _messenger;
        private readonly Mock<IAppSettingsService> _mockSettingsService;
        private readonly Mock<IThemeService> _mockThemeService;
        private readonly MainViewModel _viewModel;
        private readonly SynchronizationContext _testSynchronizationContext;

        public MainWindowViewModelTests()
        {
            // 设置测试同步上下文
            // 用同步执行的替身上下文，保证 Post 回调在断言前完成
            _testSynchronizationContext = new ImmediateSynchronizationContext();

            // 创建模拟对象
            _mockServiceManager = new Mock<IFileShareServiceManager>();
            _mockAppLifetime = new Mock<IClassicDesktopStyleApplicationLifetime>();
            _mockDialogService = new Mock<IPlatformDialogService>();
            _mockDialogs = new Mock<IDialogService>();
            _mockRegionManager = new Mock<IRegionManager>();
            // 每个测试用独立实例，避免全局单例 WeakReferenceMessenger.Default 在测试间串扰
            _messenger = new WeakReferenceMessenger();
            _mockSettingsService = new Mock<IAppSettingsService>();
            _mockThemeService = new Mock<IThemeService>();
            _mockSettingsService.SetupGet(s => s.Current).Returns(new AppSettings());

            // 设置模拟对象的行为
            _mockServiceManager.Setup(m => m.GetLocalDeviceInfo())
                .Returns(new DeviceInfo
                {
                    DeviceId = "test-device-id",
                    DeviceName = "Test Device",
                    DeviceType = DeviceType.Desktop,
                    Port = 5237,
                    IpAddress = "127.0.0.1"
                });

            // 创建ViewModel实例
            _viewModel = new MainViewModel(
                _mockServiceManager.Object,
                _mockDialogService.Object,
                _mockDialogs.Object,
                _mockRegionManager.Object,
                _messenger,
                _mockAppLifetime.Object,
                _testSynchronizationContext,
                Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                _mockSettingsService.Object,
                _mockThemeService.Object);
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
            _mockServiceManager.Verify(m => m.RefreshDevicesAsync(), Times.Once);
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
                    Port = 5237,
                    IpAddress = "127.0.0.1"
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
            // 保存目录会补上末尾分隔符（GetSafePath 的既定行为），期望值需一致
            var expectedSavePath = folderPath + Path.DirectorySeparatorChar;
            _mockServiceManager.Verify(m => m.HandleTransferRequest("test-transfer-1", true, expectedSavePath), Times.Once);
            Assert.Equal($"开始接收文件: {transferViewModel.FileName} (保存到: {expectedSavePath})", _viewModel.StatusMessage);
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
                Port = 5237,
                IpAddress = "127.0.0.1"
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

        [Fact]
        public void RetryTransfer_RemovesFailedItem_AndLetsNewTransferAppear()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "fs-retry-" + Guid.NewGuid().ToString("N") + ".txt");
            System.IO.File.WriteAllText(path, "retry content");

            try
            {
                var failed = new FileTransferInfo
                {
                    TransferId = "old-transfer",
                    FileName = "retry.txt",
                    FileSize = 14,
                    Status = TransferStatus.Failed,
                    Direction = TransferDirection.Send,
                    SenderId = "test-device-id",
                    ReceiverId = "test-device-id",
                    FilePath = path
                };
                _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, failed);
                var failedItem = Assert.Single(_viewModel.TransferTasks);

                _mockServiceManager
                    .Setup(m => m.SendSingleAsync(
                        path, It.IsAny<DeviceInfo>(), null, null, It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        var retried = new FileTransferInfo
                        {
                            TransferId = "new-transfer",
                            FileName = "retry.txt",
                            FileSize = 14,
                            Status = TransferStatus.Pending,
                            Direction = TransferDirection.Send,
                            SenderId = "test-device-id",
                            ReceiverId = "test-device-id",
                            FilePath = path
                        };
                        var completed = new FileTransferInfo
                        {
                            TransferId = "new-transfer",
                            FileName = "retry.txt",
                            FileSize = 14,
                            TransferredSize = 14,
                            Status = TransferStatus.Completed,
                            Direction = TransferDirection.Send,
                            SenderId = "test-device-id",
                            ReceiverId = "test-device-id",
                            FilePath = path
                        };
                        _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, retried);
                        _mockServiceManager.Raise(m => m.OnTransferCompleted += null, completed, null);
                        return Task.FromResult(true);
                    });

                _viewModel.RetryTransferCommand.Execute(failedItem);

                var item = Assert.Single(_viewModel.TransferTasks);
                Assert.NotSame(failedItem, item);
                Assert.DoesNotContain(failedItem, _viewModel.TransferTasks);
                Assert.DoesNotContain(failedItem, _viewModel.SentTransferTasks);
                Assert.Equal("new-transfer", item.TransferId);
                Assert.Equal(TransferStatus.Completed, item.Status);
                Assert.Single(_viewModel.SentTransferTasks);
                Assert.Same(item, Assert.Single(_viewModel.SentTransferTasks));
            }
            finally
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
        }

        [Fact]
        public void TextMessage_StillAppearsInTransferLists_OncePerDirection()
        {
            var sent = new FileTransferInfo
            {
                TransferId = "msg-send",
                FileName = "文本摘要",
                FileSize = 10,
                Status = TransferStatus.Completed,
                Direction = TransferDirection.Send,
                SenderId = "test-device-id",
                ReceiverId = "remote-device-1",
                IsMessage = true,
                MessageText = "hello"
            };
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, sent);

            Assert.Single(_viewModel.TransferTasks);
            Assert.Single(_viewModel.SentTransferTasks);
            Assert.Empty(_viewModel.ReceivedTransferTasks);
            Assert.True(_viewModel.TransferTasks[0].IsMessage);
            Assert.Equal("hello", _viewModel.TransferTasks[0].MessageText);

            var received = new FileTransferInfo
            {
                TransferId = "msg-receive",
                FileName = "文本摘要",
                FileSize = 10,
                Status = TransferStatus.Completed,
                Direction = TransferDirection.Receive,
                SenderId = "remote-device-1",
                ReceiverId = "test-device-id",
                IsMessage = true,
                MessageText = "world"
            };
            _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, received);

            Assert.Equal(2, _viewModel.TransferTasks.Count);
            Assert.Single(_viewModel.SentTransferTasks);
            Assert.Single(_viewModel.ReceivedTransferTasks);
        }

        [Fact]
        public void ConcurrentRetries_ReplaceTheirOwnFailedItems_WithoutCrossTalk()
        {
            var pathA = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "fs-retry-a-" + Guid.NewGuid().ToString("N") + ".txt");
            var pathB = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "fs-retry-b-" + Guid.NewGuid().ToString("N") + ".txt");
            System.IO.File.WriteAllText(pathA, "a");
            System.IO.File.WriteAllText(pathB, "b");

            try
            {
                var failA = new FileTransferInfo
                {
                    TransferId = "fail-a", FileName = "a.txt", FileSize = 1,
                    Status = TransferStatus.Failed, Direction = TransferDirection.Send,
                    SenderId = "test-device-id", ReceiverId = "device-A", FilePath = pathA
                };
                var failB = new FileTransferInfo
                {
                    TransferId = "fail-b", FileName = "b.txt", FileSize = 1,
                    Status = TransferStatus.Failed, Direction = TransferDirection.Send,
                    SenderId = "test-device-id", ReceiverId = "device-B", FilePath = pathB
                };
                _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, failA);
                _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, failB);
                _mockServiceManager.Raise(m => m.OnDeviceDiscovered += null, new DeviceInfo
                {
                    DeviceId = "device-A", DeviceName = "A", DeviceType = DeviceType.Desktop,
                    IpAddress = "192.168.1.10", Port = 5237
                });
                _mockServiceManager.Raise(m => m.OnDeviceDiscovered += null, new DeviceInfo
                {
                    DeviceId = "device-B", DeviceName = "B", DeviceType = DeviceType.Desktop,
                    IpAddress = "192.168.1.11", Port = 5237
                });
                var itemA = _viewModel.TransferTasks.Single(t => t.FileName == "a.txt");
                var itemB = _viewModel.TransferTasks.Single(t => t.FileName == "b.txt");

                _mockServiceManager
                    .Setup(m => m.SendSingleAsync(
                        pathA, It.IsAny<DeviceInfo>(), null, null, It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        RaiseRetrySucceeded("new-a", "a.txt", "device-A", pathA);
                        return Task.FromResult(true);
                    });
                _mockServiceManager
                    .Setup(m => m.SendSingleAsync(
                        pathB, It.IsAny<DeviceInfo>(), null, null, It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        RaiseRetrySucceeded("new-b", "b.txt", "device-B", pathB);
                        return Task.FromResult(true);
                    });

                _viewModel.RetryTransferCommand.Execute(itemA);
                _viewModel.RetryTransferCommand.Execute(itemB);

                Assert.Equal(2, _viewModel.TransferTasks.Count);
                Assert.DoesNotContain(itemA, _viewModel.TransferTasks);
                Assert.DoesNotContain(itemB, _viewModel.TransferTasks);
                var newA = _viewModel.TransferTasks.Single(t => t.FileName == "a.txt");
                var newB = _viewModel.TransferTasks.Single(t => t.FileName == "b.txt");
                Assert.NotSame(itemA, newA);
                Assert.NotSame(itemB, newB);
                Assert.Equal("new-a", newA.TransferId);
                Assert.Equal("new-b", newB.TransferId);
                Assert.Equal(TransferStatus.Completed, newA.Status);
                Assert.Equal(TransferStatus.Completed, newB.Status);
                Assert.Equal(2, _viewModel.SentTransferTasks.Count);
                Assert.DoesNotContain(itemA, _viewModel.SentTransferTasks);
                Assert.DoesNotContain(itemB, _viewModel.SentTransferTasks);
            }
            finally
            {
                foreach (var path in new[] { pathA, pathB })
                {
                    if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                    }
                }
            }

            void RaiseRetrySucceeded(string id, string fileName, string receiverId, string path)
            {
                var request = new FileTransferInfo
                {
                    TransferId = id, FileName = fileName, FileSize = 1,
                    Status = TransferStatus.Transferring, Direction = TransferDirection.Send,
                    SenderId = "test-device-id", ReceiverId = receiverId, FilePath = path
                };
                var completed = new FileTransferInfo
                {
                    TransferId = id, FileName = fileName, FileSize = 1, TransferredSize = 1,
                    Status = TransferStatus.Completed, Direction = TransferDirection.Send,
                    SenderId = "test-device-id", ReceiverId = receiverId, FilePath = path
                };
                _mockServiceManager.Raise(m => m.OnTransferRequestSendAndReceive += null, request);
                _mockServiceManager.Raise(m => m.OnTransferCompleted += null, completed, null);
            }
        }

        [Theory]
        [InlineData(true, Avalonia.Controls.WindowState.Normal, false)]
        [InlineData(true, Avalonia.Controls.WindowState.Minimized, true)]
        [InlineData(false, Avalonia.Controls.WindowState.Normal, true)]
        [InlineData(false, Avalonia.Controls.WindowState.Minimized, true)]
        public void IsBackgroundWindow_OnlyTrayOrMinimizedCounts(
            bool isVisible, Avalonia.Controls.WindowState state, bool expected)
        {
            Assert.Equal(expected, MainViewModel.IsBackgroundWindow(isVisible, state));
        }

        [Fact]
        public void ShouldShowCompletionToast_SkipsTextMessages()
        {
            Assert.False(MainViewModel.ShouldShowCompletionToast(true, true));
            Assert.True(MainViewModel.ShouldShowCompletionToast(false, true));
            Assert.False(MainViewModel.ShouldShowCompletionToast(false, false));
        }
    }
}