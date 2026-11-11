using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using FileShare.LocalSend;
using LocalSend.Core.Http.DtoV2;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace FileShare.Desktop.Tests.Services;

/// <summary>
/// LocalSend 互操作「历史只记成功」的端到端回归：
/// 成功的文本消息 / 文件发送各写一条 Completed 记录；失败与取消一律不写库，
/// 但失败原因仍通过返回值传给 UI、并在传输列表里体现。
/// 同时锁定工程约束：await 写库、异常不外抛、写库失败不影响发送结果。
/// </summary>
public class LocalSendMessageHistoryTests
{
    [Fact]
    public async Task MessageSend_IsRecordedAsCompleted()
    {
        await WithHostAsync(PeerReply.CloseWithoutAccepting, async (host, peer, captured) =>
        {
            var result = await host.SendMessageAsync(peer, "hello from pc");

            Assert.True(result.Success);
            var entity = Assert.Single(captured);
            Assert.Equal(TransferStatus.Completed, entity.Status);
            Assert.Equal(TransferDirection.Send, entity.Direction);
            Assert.Equal("hello from pc", entity.Message);
            Assert.NotNull(entity.Message);
            Assert.Null(entity.ErrorMessage);
            Assert.Null(entity.FilePath);
        });
    }

    [Fact]
    public async Task MessageSend_WhenPeerOnlyCopies_IsStillCompleted()
    {
        await WithHostAsync(PeerReply.CopyWithoutAccepting, async (host, peer, captured) =>
        {
            var result = await host.SendMessageAsync(peer, "copy me");

            Assert.True(result.Success);
            var entity = Assert.Single(captured);
            Assert.Equal(TransferStatus.Completed, entity.Status);
            Assert.Equal("copy me", entity.Message);
            Assert.Null(entity.ErrorMessage);
        });
    }

    [Fact]
    public async Task FailedFileSend_IsRecorded_WithReasonAndSourcePath()
    {
        await WithHostAsync(PeerReply.CloseWithoutAccepting, async (host, peer, captured, root) =>
        {
            var sourcePath = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(sourcePath, "fake image");

            var result = await host.SendFileAsync(peer, sourcePath);

            Assert.False(result.Success, "文件发送的既有失败判定不能被改动");
            Assert.Equal("对方未接受文件（可能拒绝了请求）", result.Error);

            // 失败的发送也落历史（用户决定恢复），带原因与源文件路径
            var entity = Assert.Single(captured);
            Assert.Equal(TransferStatus.Failed, entity.Status);
            Assert.Equal(TransferDirection.Send, entity.Direction);
            Assert.Equal("对方未接受文件（可能拒绝了请求）", entity.ErrorMessage);
            Assert.Equal(sourcePath, entity.FilePath);
            Assert.Null(entity.Message);
        });
    }

    [Fact]
    public async Task SuccessfulFileSend_IsRecordedAsCompleted()
    {
        await WithHostAsync(PeerReply.AcceptFile, async (host, peer, captured, root) =>
        {
            var sourcePath = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(sourcePath, "fake image");

            var result = await host.SendFileAsync(peer, sourcePath);

            Assert.True(result.Success, "发送失败: " + result.Error);
            var entity = Assert.Single(captured);
            Assert.Equal(TransferStatus.Completed, entity.Status);
            Assert.Equal(TransferDirection.Send, entity.Direction);
            Assert.Null(entity.Message);
            Assert.Null(entity.ErrorMessage);
            Assert.Equal(sourcePath, entity.FilePath);
        });
    }

    /// <summary>写库抛异常时不得外抛、不得改变已成功的发送结果（异常不外抛 + 留日志）。</summary>
    [Fact]
    public async Task HistoryWriteFailure_DoesNotBreakSuccessfulSend()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings { DeviceName = "TestPC" });
        var database = new Mock<IDatabaseService>();
        database.Setup(d => d.AddSingleReceiveHistoryAsync(It.IsAny<ReceiveHistoryEntity>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        using var host = new LocalSendInteropHost(
            settings.Object, new Mock<IPlatformDirectoryService>().Object,
            database.Object, NullLoggerFactory.Instance);

        var method = typeof(LocalSendInteropHost).GetMethod(
            "RecordMessageSendHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var peer = new LocalSendPeer("localsend:phone", "Phone", "127.0.0.1",
            (ushort)53317, ProtocolTypeV2.Http, string.Empty);

        var task = (Task)method!.Invoke(host, new object?[]
        {
            "文本消息",
            peer,
            TransferStatus.Completed,
            (string?)null
        })!;

        await task; // 写库抛异常也不应外传（不抛异常即为通过）
    }

    public enum PeerReply
    {
        CloseWithoutAccepting,
        CopyWithoutAccepting,
        AcceptFile,
    }

    private static async Task WithHostAsync(
        PeerReply reply,
        Func<LocalSendInteropHost, LocalSendPeer, List<ReceiveHistoryEntity>, string, Task> assertions)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = Task.Run(() => FakePeerAsync(listener, reply, cts.Token), cts.Token);

        var root = Path.Combine(Path.GetTempPath(), "ls-msghist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var captured = new List<ReceiveHistoryEntity>();
        try
        {
            var settings = new Mock<IAppSettingsService>();
            settings.SetupGet(s => s.Current).Returns(new AppSettings { DeviceName = "TestPC" });
            var database = new Mock<IDatabaseService>();
            database
                .Setup(d => d.AddSingleReceiveHistoryAsync(It.IsAny<ReceiveHistoryEntity>()))
                .Callback<ReceiveHistoryEntity>(captured.Add)
                .ReturnsAsync(true);

            using var host = new LocalSendInteropHost(
                settings.Object, new Mock<IPlatformDirectoryService>().Object,
                database.Object, NullLoggerFactory.Instance);

            var identity = LocalSendIdentity.LoadOrCreate(Path.Combine(root, "identity.json"));
            var trust = LocalSendTrustStore.LoadOrCreate(Path.Combine(root, "trust.json"));
            var sender = new LocalSendSender("TestPC", identity, trust,
                NullLogger<LocalSendSender>.Instance);

            // 通过反射注入 sender，避免启动真实的 LocalSend 服务端 / 组播
            var field = typeof(LocalSendInteropHost).GetField("_sender",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(host, sender);

            var peer = new LocalSendPeer("localsend:phone", "Phone", "127.0.0.1",
                (ushort)port, ProtocolTypeV2.Http, string.Empty);

            await assertions(host, peer, captured, root);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    private static Task WithHostAsync(
        PeerReply reply,
        Func<LocalSendInteropHost, LocalSendPeer, List<ReceiveHistoryEntity>, Task> assertions) =>
        WithHostAsync(reply, (host, peer, captured, _) => assertions(host, peer, captured));

    private static async Task FakePeerAsync(TcpListener listener, PeerReply reply, CancellationToken ct)
    {
        var clients = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                clients.Add(HandleAsync(client, reply, ct));
            }
        }
        finally
        {
            foreach (var c in clients)
            {
                try { await c.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { /* ignore */ }
            }
        }
    }

    private static async Task HandleAsync(TcpClient client, PeerReply reply, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var header = await ReadHeaderAsync(stream, ct);
            if (header is null) return;
            var headText = header;

            var isPrepare = headText.Contains("/prepare-upload", StringComparison.Ordinal);
            if (!isPrepare)
            {
                // upload：必须先把请求体读完再回响应，否则客户端正在写就被断连，
                // 会报 "An error occurred while sending the request"。
                // StreamContent 包的是不可 seek 的流，因此走 chunked 而非 Content-Length。
                await DrainRequestBodyAsync(stream, headText, ct);
                var ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(ok), ct);
                await stream.FlushAsync(ct);
                return;
            }

            await DrainRequestBodyAsync(stream, headText, ct);

            string raw;
            if (reply == PeerReply.AcceptFile)
            {
                const string json = """{"sessionId":"s","files":{"1":"t"}}""";
                raw = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                      + json.Length + "\r\nConnection: close\r\n\r\n" + json;
            }
            else if (reply == PeerReply.CopyWithoutAccepting)
            {
                const string json = """{"sessionId":"s","files":{}}""";
                raw = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                      + json.Length + "\r\nConnection: close\r\n\r\n" + json;
            }
            else
            {
                raw = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(raw), ct);
            await stream.FlushAsync(ct);
        }
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < 4 || !(header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n'))
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0 || header.Count > 64 * 1024) return null;
            header.Add(one[0]);
        }
        return Encoding.ASCII.GetString(header.ToArray());
    }

    private static async Task DrainRequestBodyAsync(NetworkStream stream, string headText, CancellationToken ct)
    {
        var contentLength = 0;
        foreach (var line in headText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[15..].Trim(), out var v)) { contentLength = v; break; }
        }

        var buffer = new byte[4096];
        while (contentLength > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, contentLength)), ct);
            if (read == 0) break;
            contentLength -= read;
        }

        if (contentLength > 0 || !headText.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // chunked 终止块："0\r\n\r\n"。逐字节读到该序列，避免提前关闭连接。
        var tail = new List<byte>(5);
        var one = new byte[1];
        for (var i = 0; i < 1024 * 1024; i++)
        {
            if (await stream.ReadAsync(one, ct) == 0) return;
            tail.Add(one[0]);
            if (tail.Count > 5) tail.RemoveAt(0);
            if (tail.Count == 5 && tail[0] == (byte)'0' && tail[1] == (byte)'\r'
                && tail[2] == (byte)'\n' && tail[3] == (byte)'\r' && tail[4] == (byte)'\n')
            {
                return;
            }
        }
    }
}
