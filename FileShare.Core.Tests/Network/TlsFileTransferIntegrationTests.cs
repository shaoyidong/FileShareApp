using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Network.Tls;
using FileShare.Core.Services;
using Moq;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// TLS 端到端集成测试：两个均启用 TLS 的 TcpFileTransferService 通过 SslStream 加密通道回环传输文件。
/// 验证：双向 TLS 握手成功、TOFU 首次信任生效、文件内容一致、传输度量正确。
/// </summary>
public class TlsFileTransferIntegrationTests
{
    private readonly Mock<IPlatformDirectoryService> _mockDirectoryService;

    public TlsFileTransferIntegrationTests()
    {
        _mockDirectoryService = new Mock<IPlatformDirectoryService>();
        var tempRoot = Path.Combine(Path.GetTempPath(), "FileShareTlsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        _mockDirectoryService.Setup(d => d.GetDownloadsDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetPicturesDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetVideosDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetMusicDirectory()).Returns(tempRoot);
        _mockDirectoryService.Setup(d => d.GetDocumentsDirectory()).Returns(tempRoot);
    }

    /// <summary>
    /// 双方启用 TLS 时，文件经加密通道完整传输，且首连 TOFU 信任建立。
    /// </summary>
    [Fact]
    public async Task SendFileAsync_WithTlsEnabled_TransfersFileOverEncryptedChannel()
    {
        // Arrange：为发送方与接收方分别准备独立的证书目录与指纹库（避免互相污染）
        var tempDir = _mockDirectoryService.Object.GetDownloadsDirectory();
        var senderTlsDir = Path.Combine(tempDir, "sender-tls");
        var receiverTlsDir = Path.Combine(tempDir, "receiver-tls");
        Directory.CreateDirectory(senderTlsDir);
        Directory.CreateDirectory(receiverTlsDir);

        var senderTls = new TlsOptions
        {
            Enabled = true,
            CertificateDirectory = senderTlsDir,
            FingerprintStorePath = Path.Combine(senderTlsDir, "fingerprints.txt")
        };
        var receiverTls = new TlsOptions
        {
            Enabled = true,
            CertificateDirectory = receiverTlsDir,
            FingerprintStorePath = Path.Combine(receiverTlsDir, "fingerprints.txt")
        };

        var senderPort = GetFreePort();
        var receiverPort = GetFreePort();
        using var sender = new TcpFileTransferService(_mockDirectoryService.Object, senderPort, null, senderTls);
        using var receiver = new TcpFileTransferService(_mockDirectoryService.Object, receiverPort, null, receiverTls);

        // 断言双方均已启用 TLS（证书生成成功）
        Assert.True(sender.TlsEnabled, "发送方应启用 TLS");
        Assert.True(receiver.TlsEnabled, "接收方应启用 TLS");

        // 源文件独立子目录，避免与接收文件写锁冲突
        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "tls_source_" + Guid.NewGuid().ToString("N") + ".bin");
        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);

        var receivedSignal = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.OnTransferRequestSendAndReceive += info =>
        {
            receiver.HandleTransferRequest(info.TransferId, accept: true, savePath: tempDir);
        };
        receiver.OnTransferCompleted += (info, msg) =>
        {
            if (info.Status == TransferStatus.Completed)
                receivedSignal.TrySetResult(msg);
            else
                receivedSignal.TrySetResult($"未完成: {info.Status} {msg}");
        };

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
                Port = receiverPort,
                SupportsTls = true // 关键：发送方据此升级到 SslStream
            };

            // Act
            var success = await sender.SendFileAsync(sourceFile, targetDevice, "sender-device");

            // Assert
            if (!success)
            {
                await Task.WhenAny(senderCompleted.Task, Task.Delay(3000));
                var (status, msg) = senderCompleted.Task.IsCompleted
                    ? senderCompleted.Task.Result
                    : (TransferStatus.Failed, "未触发完成事件");
                Assert.Fail($"TLS 传输失败。发送方状态={status}, 消息={msg}");
            }

            await Task.WhenAny(receivedSignal.Task, Task.Delay(10000));
            var receivedMessage = receivedSignal.Task.IsCompleted ? receivedSignal.Task.Result : "超时未收到完成事件";
            Assert.DoesNotContain("未完成", receivedMessage ?? "");
            Assert.DoesNotContain("超时", receivedMessage ?? "");

            // 文件内容一致
            var receivedFile = Path.Combine(tempDir, Path.GetFileName(sourceFile));
            Assert.True(File.Exists(receivedFile), "接收文件应存在");
            var receivedData = await File.ReadAllBytesAsync(receivedFile);
            Assert.Equal(data, receivedData);

            // 度量：加密通道下累计字节仍应正确统计
            Assert.True(sender.TotalBytesSent >= data.Length, $"TotalBytesSent={sender.TotalBytesSent} 应 >= {data.Length}");
            Assert.True(receiver.TotalBytesReceived >= data.Length, $"TotalBytesReceived={receiver.TotalBytesReceived} 应 >= {data.Length}");

            // TOFU 首次信任：发送方指纹库应记录接收方指纹，接收方指纹库应记录发送方指纹
            Assert.True(File.Exists(senderTls.FingerprintStorePath), "发送方指纹库应已创建");
            Assert.True(File.Exists(receiverTls.FingerprintStorePath), "接收方指纹库应已创建");
            var senderFpContent = await File.ReadAllTextAsync(senderTls.FingerprintStorePath);
            var receiverFpContent = await File.ReadAllTextAsync(receiverTls.FingerprintStorePath);
            Assert.Contains("receiver-device", senderFpContent);
            Assert.Contains("sender-device", receiverFpContent);
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
