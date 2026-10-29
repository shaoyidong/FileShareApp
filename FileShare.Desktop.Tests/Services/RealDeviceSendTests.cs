using FileShare.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileShare.Desktop.Tests.Services;

/// <summary>
/// 真机联调（默认跳过）：设置环境变量 LS_PEER_IP=手机IP 后运行，
/// 探测并发送一个临时文件到手机上的官方 LocalSend。
/// </summary>
public class RealDeviceSendTests
{
    [Fact]
    public async Task SendToPhone()
    {
        var ip = Environment.GetEnvironmentVariable("LS_PEER_IP");
        if (string.IsNullOrWhiteSpace(ip))
        {
            Assert.Skip("未设置 LS_PEER_IP，跳过真机联调");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "FileSharePeer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var identity = LocalSendIdentity.LoadOrCreate(Path.Combine(root, "identity.json"));
        var trust = LocalSendTrustStore.LoadOrCreate(Path.Combine(root, "trust.json"));
        var sender = new LocalSendSender("FileShare-PC", identity, trust, NullLogger<LocalSendSender>.Instance);

        var peer = await sender.ProbeAsync(ip, 53317);
        Assert.NotNull(peer);
        Console.WriteLine($"探测到设备: {peer!.Alias} {peer.IpAddress}:{peer.Port} {peer.Protocol} 指纹={peer.Fingerprint}");

        var filePath = Path.Combine(root, "来自电脑.txt");
        await File.WriteAllTextAsync(filePath, "hello from FileShare desktop " + DateTime.Now.ToString("s"));

        var result = await sender.SendAsync(peer, filePath);
        Console.WriteLine($"发送结果: success={result.Success} error={result.Error}");
        Assert.True(result.Success, "发送失败: " + result.Error);
    }
}