using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Moq;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 端到端集成测试：验证 Channel&lt;T&gt; 重构后的真实文件传输（发送→接收→校验）。
/// 使用 127.0.0.1 上的两个 TcpFileTransferService 实例进行回环传输。
/// </summary>
public class TcpFileTransferIntegrationTests
{
    private readonly Mock<IPlatformDirectoryService> _mockDirectoryService;

    public TcpFileTransferIntegrationTests()
    {
        _mockDirectoryService = new Mock<IPlatformDirectoryService>();
        var tempRoot = Path.Combine(Path.GetTempPath(), "FileShareTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        _mockDirectoryService.Setup(d => d.GetDownloadsDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetPicturesDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetVideosDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetMusicDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetDocumentsDirectory()).Returns(tempRoot);
    }

    /// <summary>
    /// 完整传输一个小文件，验证内容一致性与传输度量（累计发送/接收字节数）。
    /// </summary>
    [Fact]
    public async Task SendFileAsync_TransfersFile_EndToEndWithMetrics()
    {
        // Arrange: 准备发送方与接收方（不同端口，回环地址）
        var senderPort = GetFreePort();
        var receiverPort = GetFreePort();
        using var sender = new TcpFileTransferService(_mockDirectoryService.Object, senderPort);
        using var receiver = new TcpFileTransferService(_mockDirectoryService.Object, receiverPort);

        var tempDir = _mockDirectoryService.Object.GetDownloadsDirectory();
        // 源文件放在独立子目录，避免与接收方写入的同名文件路径冲突（接收方持有写锁时会阻止发送方读取）
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "source_" + Guid.NewGuid().ToString("N") + ".bin");
        // 写入 256KB 数据（多块 64KB 缓冲，覆盖 Channel 多轮生产/消费）
        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);

        var receivedSignal = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 接收方：自动接受传输请求
        receiver.OnTransferRequestSendAndReceive += info =>
        {
            receiver.HandleTransferRequest(info.TransferId, accept: true, savePath: tempDir);
        };
        receiver.OnTransferCompleted += (info, msg) =>
        {
            if (info.Status == TransferStatus.Completed)
            {
                receivedSignal.TrySetResult(msg);
            }
            else
            {
                receivedSignal.TrySetResult($"未完成: {info.Status} {msg}");
            }
        };

        // 发送方完成事件，便于诊断失败原因
        var senderCompleted = new TaskCompletionSource<(TransferStatus Status, string? Msg)>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.OnTransferCompleted += (info, msg) => senderCompleted.TrySetResult((info.Status, msg));

        await receiver.StartAsync();
        await sender.StartAsync();
        try
        {
            var targetDevice = new DeviceInfo
            {
                DeviceId = "receiver-device",
                DeviceName = "Receiver",
                DeviceType = DeviceType.Desktop,
                IpAddress = "127.0.0.1",
                Port = receiverPort
            };

            // Act: 发送文件
            var success = await sender.SendFileAsync(sourceFile, targetDevice, "sender-device");

            // Assert: 传输成功
            if (!success)
            {
                await Task.WhenAny(senderCompleted.Task, Task.Delay(3000));
                var (status, msg) = senderCompleted.Task.IsCompleted
                    ? senderCompleted.Task.Result
                    : (TransferStatus.Failed, "未触发完成事件");
                Assert.Fail($"SendFileAsync 返回 false。发送方状态={status}, 消息={msg}");
            }

            await Task.WhenAny(receivedSignal.Task, Task.Delay(10000));
            var receivedMessage = receivedSignal.Task.IsCompleted ? receivedSignal.Task.Result : "超时未收到完成事件";
            Assert.DoesNotContain("未完成", receivedMessage ?? "");
            Assert.DoesNotContain("超时", receivedMessage ?? "");

            // 接收的文件内容一致
            var receivedFile = Path.Combine(tempDir, Path.GetFileName(sourceFile));
            Assert.True(File.Exists(receivedFile), "接收文件应存在");
            var receivedData = await File.ReadAllBytesAsync(receivedFile);
            Assert.Equal(data, receivedData);

            // 传输度量：累计发送/接收字节数应 >= 文件大小
            Assert.True(sender.TotalBytesSent >= data.Length, $"TotalBytesSent={sender.TotalBytesSent} 应 >= {data.Length}");
            Assert.True(receiver.TotalBytesReceived >= data.Length, $"TotalBytesReceived={receiver.TotalBytesReceived} 应 >= {data.Length}");
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
            TryCleanup(sourceFile);
        }
    }

    /// <summary>
    /// 接收方拒绝传输请求时，发送方应返回 false 并触发完成事件。
    /// </summary>
    [Fact]
    public async Task SendFileAsync_WhenReceiverRejects_ReturnsFalse()
    {
        // Arrange
        var senderPort = GetFreePort();
        var receiverPort = GetFreePort();
        using var sender = new TcpFileTransferService(_mockDirectoryService.Object, senderPort);
        using var receiver = new TcpFileTransferService(_mockDirectoryService.Object, receiverPort);

        var tempDir = _mockDirectoryService.Object.GetDownloadsDirectory();
        var sourceFile = Path.Combine(tempDir, "reject_" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(sourceFile, new byte[1024]);

        // 接收方：自动拒绝
        receiver.OnTransferRequestSendAndReceive += info =>
        {
            receiver.HandleTransferRequest(info.TransferId, accept: false);
        };
        // 发送方完成事件
        var senderCompleted = new TaskCompletionSource<TransferStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.OnTransferCompleted += (info, msg) => senderCompleted.TrySetResult(info.Status);

        await receiver.StartAsync();
        await sender.StartAsync();
        try
        {
            var targetDevice = new DeviceInfo
            {
                DeviceId = "receiver-device",
                DeviceName = "Receiver",
                DeviceType = DeviceType.Desktop,
                IpAddress = "127.0.0.1",
                Port = receiverPort
            };

            // Act
            var success = await sender.SendFileAsync(sourceFile, targetDevice, "sender-device");

            // Assert
            Assert.False(success, "被拒绝时应返回 false");
            await Task.WhenAny(senderCompleted.Task, Task.Delay(5000));
            Assert.True(senderCompleted.Task.IsCompleted, "应触发发送方完成事件");
            Assert.Equal(TransferStatus.Cancelled, senderCompleted.Task.Result);
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
            TryCleanup(sourceFile);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryCleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
