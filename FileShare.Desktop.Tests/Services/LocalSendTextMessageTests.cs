using FileShare.LocalSend;
using LocalSend.Core.Http.DtoV2;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileShare.Desktop.Tests.Services;

/// <summary>
/// 文本消息与文件发送「成功判定」必须彻底分开的回归测试。
/// <para>
/// 用户实证：手机端原版 LocalSend 已弹出文本消息、用户点「关闭」或「复制」，
/// PC 端却把它记成失败并显示「对方未接受文件（可能拒绝了请求）」。
/// 根因是文本消息沿用了文件请求的 prepare-upload 判定：对端不接受那个附带的
/// .txt 文件（204 / 空 files / 4xx）被当成「消息发送失败」。
/// 实际上正文在 Preview 字段里随请求一起送达，对端已收到即应记为成功。
/// </para>
/// </summary>
public class LocalSendTextMessageTests
{
    /// <summary>假对端对 prepare-upload 的响应方式。</summary>
    public enum PeerReply
    {
        /// <summary>200 + token，随后 upload 不回任何响应（客户端弹窗后不确认）。</summary>
        TokenButNoUploadResponse,

        /// <summary>204 No Content —— 对端展示消息后点「关闭」，不接受附带文件。</summary>
        CloseWithoutAccepting,

        /// <summary>200 但 files 为空 —— 对端点「复制」，只取正文不接收文件。</summary>
        CopyWithoutAccepting,

        /// <summary>403 —— 对端明确拒绝附带文件。</summary>
        DeclineFile,
    }

    [Theory]
    [InlineData(PeerReply.TokenButNoUploadResponse)]
    [InlineData(PeerReply.CloseWithoutAccepting)]
    [InlineData(PeerReply.CopyWithoutAccepting)]
    [InlineData(PeerReply.DeclineFile)]
    public async Task SendMessage_IsDelivered_RegardlessOfHowPeerHandlesTheAttachedFile(PeerReply reply)
    {
        await WithFakePeerAsync(reply, async (sender, peer) =>
        {
            var clock = Stopwatch.StartNew();
            var result = await sender.SendMessageAsync(peer, "hello from pc");
            clock.Stop();

            Assert.True(result.Success,
                $"文本消息正文已随 Preview 送达，对端 {reply} 不能判为失败: {result.Error}");
            Assert.True(string.IsNullOrEmpty(result.Error),
                "成功时不应带任何失败原因");
            AssertNoFileSemantics(result.Error);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20),
                $"文本消息不应等待过久，实际 {clock.Elapsed.TotalSeconds:F1}s");
        });
    }

    [Fact]
    public async Task SendMessage_NeverUsesFileRejectionWording()
    {
        // 用户明确要求：文本消息不得出现「对方未接受文件（可能拒绝了请求）」
        await WithFakePeerAsync(PeerReply.CloseWithoutAccepting, async (sender, peer) =>
        {
            var result = await sender.SendMessageAsync(peer, "文本消息不是文件发送请求");
            Assert.True(result.Success);
            AssertNoFileSemantics(result.Error);
        });
    }

    [Theory]
    [InlineData(PeerReply.CloseWithoutAccepting, "对方未接受文件（可能拒绝了请求）")]
    [InlineData(PeerReply.CopyWithoutAccepting, "对方拒绝了该文件")]
    public async Task SendFile_KeepsExistingFailureJudgement(PeerReply reply, string expectedError)
    {
        // 文件发送的既有逻辑必须原样保留：对端不接受文件就是失败，
        // 且仍返回原有的文件语义错误文案。
        await WithFakePeerAsync(reply, async (sender, peer, root) =>
        {
            var sourcePath = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(sourcePath, "fake image bytes");

            var result = await sender.SendAsync(peer, sourcePath);

            Assert.False(result.Success, "文件发送未被对端接受，仍应判失败");
            Assert.Equal(expectedError, result.Error);
        });
    }

    private static void AssertNoFileSemantics(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return;
        }

        Assert.DoesNotContain("对方未接受文件", error);
        Assert.DoesNotContain("对方拒绝了该文件", error);
    }

    /// <summary>启动假对端，执行断言，统一清理资源。</summary>
    private static async Task WithFakePeerAsync(
        PeerReply reply,
        Func<LocalSendSender, LocalSendPeer, string, Task> assertions)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = Task.Run(() => FakePeerAsync(listener, reply, cts.Token), cts.Token);

        var root = Path.Combine(Path.GetTempPath(), "ls-textmsg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var identity = LocalSendIdentity.LoadOrCreate(Path.Combine(root, "identity.json"));
            var trust = LocalSendTrustStore.LoadOrCreate(Path.Combine(root, "trust.json"));
            var sender = new LocalSendSender("Sender", identity, trust,
                NullLogger<LocalSendSender>.Instance);
            var peer = new LocalSendPeer("localsend:text-msg", "Phone", "127.0.0.1",
                (ushort)port, ProtocolTypeV2.Http, string.Empty);

            await assertions(sender, peer, root);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            TryDelete(root);
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* 假对端可能故意挂起，取消即可 */ }
        }
    }

    private static Task WithFakePeerAsync(
        PeerReply reply,
        Func<LocalSendSender, LocalSendPeer, Task> assertions) =>
        WithFakePeerAsync(reply, (sender, peer, _) => assertions(sender, peer));

    /// <summary>
    /// 极简 HTTP 假对端：按 <paramref name="reply"/> 回复 prepare-upload；
    /// upload 一律不写响应，模拟官方客户端「已经弹出消息但迟迟不回 HTTP 响应」。
    /// </summary>
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
            foreach (var client in clients)
            {
                try { await client.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { /* ignore */ }
            }
        }
    }

    private static async Task HandleAsync(TcpClient client, PeerReply reply, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var header = new List<byte>();
            var one = new byte[1];
            while (!EndsWithHeaderTerminator(header))
            {
                var read = await stream.ReadAsync(one, ct);
                if (read == 0 || header.Count > 64 * 1024) return;
                header.Add(one[0]);
            }

            var headText = Encoding.ASCII.GetString(header.ToArray());
            var isPrepare = headText.Contains("/prepare-upload", StringComparison.Ordinal);
            if (!isPrepare)
            {
                // upload：读请求体但绝不写响应，连接保持到测试取消
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { }
                return;
            }

            await DrainBodyAsync(stream, headText, ct);

            var response = reply switch
            {
                PeerReply.TokenButNoUploadResponse => Ok("""{"sessionId":"session-1","files":{"1":"token-1"}}"""),
                PeerReply.CopyWithoutAccepting => Ok("""{"sessionId":"session-1","files":{}}"""),
                PeerReply.CloseWithoutAccepting => "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                PeerReply.DeclineFile => "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                _ => Ok("""{"sessionId":"session-1","files":{}}"""),
            };

            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
    }

    private static string Ok(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
               + body.Length + "\r\nConnection: close\r\n\r\n" + json;
    }

    private static async Task DrainBodyAsync(NetworkStream stream, string headText, CancellationToken ct)
    {
        var contentLength = 0;
        foreach (var line in headText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[15..].Trim(), out var value))
            {
                contentLength = value;
                break;
            }
        }

        var buffer = new byte[4096];
        var remaining = contentLength;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) break;
            remaining -= read;
        }
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
    {
        var n = bytes.Count;
        return n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n'
                      && bytes[n - 2] == '\r' && bytes[n - 1] == '\n';
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* 清理失败不影响断言 */ }
    }
}
