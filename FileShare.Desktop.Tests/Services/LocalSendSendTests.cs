using FileShare.Core.Services;
using FileShare.Desktop.Services;
using LocalSend.Core.Crypto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net.Http;

namespace FileShare.Desktop.Tests.Services;

/// <summary>
/// LocalSend 发送方向端到端测试：进程内起 LocalSend v2 服务端（模拟官方客户端），
/// 覆盖 HTTP、HTTPS + mTLS、以及指纹不匹配（TOFU 拒绝）三种情况。
/// </summary>
public class LocalSendSendTests
{
    [Fact]
    public async Task SendFile_OverHttp_SavesFile()
    {
        var root = CreateRoot();
        try
        {
            var receiveDirectory = Path.Combine(root, "received");
            Directory.CreateDirectory(receiveDirectory);
            var sourcePath = Path.Combine(root, "send-me.txt");
            await File.WriteAllTextAsync(sourcePath, "hello localsend");

            var port = GetFreePort();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = StartServer(port, tls: null, out _);
            var serverTask = server.StartAsync(cts.Token);
            var accept = StartAcceptLoop(server, receiveDirectory, cts.Token);

            await WaitForServerAsync(port, cts.Token, serverTask);

            var sender = CreateSender(root);
            var peer = new LocalSendPeer("localsend:http", "Receiver", "127.0.0.1", (ushort)port, ProtocolTypeV2.Http, string.Empty);

            var result = await sender.SendAsync(peer, sourcePath);

            Assert.True(result.Success, "发送失败: " + result.Error);
            await AssertFileAsync(receiveDirectory, "send-me.txt", "hello localsend");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact(Skip = "依赖 Windows 密钥容器，本机拒绝访问")]
    public async Task SendFile_OverHttps_WithMutualTls_SavesFile()
    {
        var root = CreateRoot();
        try
        {
            var receiveDirectory = Path.Combine(root, "received");
            Directory.CreateDirectory(receiveDirectory);
            var sourcePath = Path.Combine(root, "secure.txt");
            await File.WriteAllTextAsync(sourcePath, "encrypted hello");

            var (certPem, keyPem, fingerprint) = TlsCertificateGenerator.Generate();
            var port = GetFreePort();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = StartServer(port, new TlsConfig { Cert = certPem, PrivateKey = keyPem }, out _);
            _ = StartAcceptLoop(server, receiveDirectory, cts.Token);

            await WaitForServerAsync(port, cts.Token);

            var sender = CreateSender(root);
            var peer = new LocalSendPeer("localsend:" + fingerprint, "SecureReceiver", "127.0.0.1", (ushort)port, ProtocolTypeV2.Https, fingerprint);

            var result = await sender.SendAsync(peer, sourcePath);

            Assert.True(result.Success, "HTTPS 发送失败: " + result.Error);
            await AssertFileAsync(receiveDirectory, "secure.txt", "encrypted hello");

            // TOFU：首次成功后应记录该对端指纹
            var trustStore = LocalSendTrustStore.LoadOrCreate(Path.Combine(root, "trust.json"));
            Assert.Equal(1, trustStore.Count);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact(Skip = "依赖 Windows 密钥容器，本机拒绝访问")]
    public async Task SendFile_OverHttps_FingerprintMismatch_IsRefused()
    {
        var root = CreateRoot();
        try
        {
            var receiveDirectory = Path.Combine(root, "received");
            Directory.CreateDirectory(receiveDirectory);
            var sourcePath = Path.Combine(root, "blocked.txt");
            await File.WriteAllTextAsync(sourcePath, "should not arrive");

            var (certPem, keyPem, _) = TlsCertificateGenerator.Generate();
            var port = GetFreePort();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = StartServer(port, new TlsConfig { Cert = certPem, PrivateKey = keyPem }, out _);
            _ = StartAcceptLoop(server, receiveDirectory, cts.Token);

            await WaitForServerAsync(port, cts.Token);

            var sender = CreateSender(root);

            // 对端声明的指纹与证书不一致 → 必须在握手阶段被拒绝
            var peer = new LocalSendPeer(
                "localsend:forged",
                "Forged",
                "127.0.0.1",
                (ushort)port,
                ProtocolTypeV2.Https,
                "0000000000000000000000000000000000000000000000000000000000000000");

            var result = await sender.SendAsync(peer, sourcePath);

            Assert.False(result.Success);
            Assert.Contains("指纹", result.Error);
            Assert.False(File.Exists(Path.Combine(receiveDirectory, "blocked.txt")), "不应向不可信对端发送任何文件");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static LocalSendServer StartServer(int port, TlsConfig? tls, out string fingerprint)
    {
        var identity = LocalSendIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "FileShareLsServer_" + Guid.NewGuid().ToString("N") + ".json"));
        fingerprint = identity.Fingerprint;

        return new LocalSendServer(
            port: (ushort)port,
            tlsConfig: tls,
            info: new ClientInfo
            {
                Alias = "Receiver",
                Version = "2.3",
                DeviceType = LocalSend.Core.Model.DeviceType.Desktop,
                Token = tls is null ? "http-fingerprint" : fingerprint
            },
            internalConfig: null,
            v2Config: new ServerConfigV2(),
            webSendConfig: null);
    }

    private static Task StartAcceptLoop(LocalSendServer server, string receiveDirectory, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var v2 = server.State.V2;
            if (v2 is null)
            {
                return;
            }

            await foreach (var evt in v2.EventTx.Reader.ReadAllAsync(ct))
            {
                switch (evt)
                {
                    case ServerEventV2.PrepareUpload prepare:
                        prepare.DecisionTx.TrySetResult(new PrepareUploadDecisionV2.Accept(prepare.Files.Keys.ToHashSet()));
                        break;

                    case ServerEventV2.FileUpload upload:
                        Directory.CreateDirectory(receiveDirectory);
                        var target = new FileUploadTarget.PathTarget(
                            Path.Combine(receiveDirectory, upload.File.FileName),
                            new TaskCompletionSource<Result<Unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously));
                        upload.TargetTx.TrySetResult(target);
                        break;
                }
            }
        });
    }

    private static LocalSendSender CreateSender(string root)
    {
        var identity = LocalSendIdentity.LoadOrCreate(Path.Combine(root, "sender-identity.json"));
        var trustStore = LocalSendTrustStore.LoadOrCreate(Path.Combine(root, "trust.json"));
        return new LocalSendSender("Sender", identity, trustStore, NullLogger<LocalSendSender>.Instance);
    }

    private static async Task AssertFileAsync(string directory, string fileName, string expectedContent)
    {
        var path = Path.Combine(directory, fileName);
        await WaitForAsync(() => Task.FromResult(File.Exists(path)), TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(path), "对端应保存文件");
        Assert.Equal(expectedContent, await File.ReadAllTextAsync(path));
    }

    [Fact(Skip = "依赖 Windows 密钥容器，本机拒绝访问")]
    public async Task ProbePeer_RecognizesLocalSendServer()
    {
        var root = CreateRoot();
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (certPem, keyPem, fingerprint) = TlsCertificateGenerator.Generate();
        using var server = new LocalSendServer(
            port: (ushort)port,
            tlsConfig: new TlsConfig { Cert = certPem, PrivateKey = keyPem },
            info: new ClientInfo
            {
                Alias = "ProbeTarget",
                Version = "2.3",
                DeviceType = LocalSend.Core.Model.DeviceType.Mobile,
                Token = fingerprint
            },
            internalConfig: null,
            v2Config: new ServerConfigV2(),
            webSendConfig: null);

        try
        {
            _ = server.StartAsync(cts.Token);
            await WaitForServerAsync(port, cts.Token);

            var settings = new Mock<IAppSettingsService>();
            settings.SetupGet(s => s.Current).Returns(new AppSettings());

            using var host = new LocalSendInteropHost(
                settings.Object,
                new Mock<IPlatformDirectoryService>().Object,
                new Mock<IDatabaseService>().Object,
                NullLoggerFactory.Instance);

            var sender = CreateSender(root);
            var peer = await sender.ProbeAsync("127.0.0.1", port);

            Assert.NotNull(peer);
            Assert.Equal("ProbeTarget", peer!.Alias);
            Assert.Equal(ProtocolTypeV2.Https, peer.Protocol);
            Assert.Equal(fingerprint, peer.Fingerprint);
        }
        finally
        {
            cts.Cancel();
            server.Dispose();
            TryDelete(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "FileShareLocalSendSend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响断言
        }
    }

    private static async Task WaitForServerAsync(int port, CancellationToken ct, Task? serverTask = null)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
                return;
            }
            catch (Exception)
            {
                // 还没起来
            }

            if (serverTask is { IsFaulted: true })
            {
                throw new InvalidOperationException("LocalSend 服务端启动失败: " + serverTask.Exception);
            }

            await Task.Delay(100, ct);
        }

        throw new InvalidOperationException("LocalSend 服务端未在预期时间内启动");
    }
    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
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
