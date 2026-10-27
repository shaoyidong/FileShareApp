using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Moq;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// TLS 端到端集成测试：两个都启用 TLS 的 <see cref="TcpFileTransferService"/> 通过 SslStream 加密通道回环传输文件。
/// <para>验证：双向 TLS 握手 + TOFU 首次信任 + 文件内容一致 + 累计字节数统计。</para>
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

    [Fact]
    public async Task SendFileAsync_WithTlsEnabled_TransfersFileOverEncryptedChannel()
    {
        var tempDir = _mockDirectoryService.Object.GetDownloadsDirectory();
        var senderPort = GetFreePort();
        var receiverPort = GetFreePort();

        using var sender = new TcpFileTransferService("sender-device", _mockDirectoryService.Object, senderPort, tlsEnabled: true);
        using var receiver = new TcpFileTransferService("receiver-device", _mockDirectoryService.Object, receiverPort, tlsEnabled: true);

        if (!sender.TlsEnabled || !receiver.TlsEnabled)
        {
            // 极少数环境（无写权限的密钥容器 / 受限沙箱）无法创建自签名证书，此时服务会降级为裸 TCP，
            // TLS 场景无法验证，直接跳过而不是给出假阳性/假阴性。
            Assert.Skip("当前环境无法初始化 TLS 证书（CryptoManager 降级为裸 TCP），跳过 TLS 集成测试。");
        }

        var sourceDir = Path.Combine(tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "tls_" + Guid.NewGuid().ToString("N") + ".bin");
        var data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);

        var receiverCompleted = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderCompleted = new TaskCompletionSource<(TransferStatus Status, string? Message)>(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.OnTransferRequestSendAndReceive += info =>
        {
            receiver.HandleTransferRequest(info.TransferId, accept: true, savePath: tempDir);
        };
        receiver.OnTransferCompleted += (info, message) =>
        {
            if (info.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
            {
                receiverCompleted.TrySetResult(message);
            }
        };
        sender.OnTransferCompleted += (info, message) => senderCompleted.TrySetResult((info.Status, message));

        await receiver.StartAsync();
        await sender.StartAsync();

        try
        {
            var target = new DeviceInfo
            {
                DeviceId = "receiver-device",
                DeviceName = "Receiver",
                DeviceType = DeviceType.Desktop,
                IpAddress = "127.0.0.1",
                Port = receiverPort,
                SupportsTls = true
            };

            var success = await sender.SendFileAsync(sourceFile, target, "sender-device", null, null);

            if (!success)
            {
                await Task.WhenAny(senderCompleted.Task, Task.Delay(5000));
                var cause = senderCompleted.Task.IsCompleted
                    ? $"{senderCompleted.Task.Result.Status} / {senderCompleted.Task.Result.Message}"
                    : "未触发完成事件";
                Assert.Fail($"TLS 传输失败：{cause}");
            }

            await Task.WhenAny(receiverCompleted.Task, Task.Delay(10000));
            Assert.True(receiverCompleted.Task.IsCompleted, "接收方应在超时前完成");

            var receivedFile = Path.Combine(tempDir, Path.GetFileName(sourceFile));
            Assert.True(File.Exists(receivedFile), "接收到的文件应存在");
            Assert.Equal(data, await File.ReadAllBytesAsync(receivedFile));

            Assert.True(sender.TotalBytesSent >= data.Length);
            Assert.True(receiver.TotalBytesReceived >= data.Length);
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
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
}
