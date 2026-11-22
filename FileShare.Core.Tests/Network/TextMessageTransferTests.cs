using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Microsoft.Data.Sqlite;
using Moq;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 原生文本消息：协议端到端（免确认 / 不落盘 / 全文可达）+ 历史 Message 列迁移与持久化。
/// </summary>
public class TextMessageTransferTests
{
    [Fact]
    public async Task SendTextMessageAsync_DeliversFullText_WithoutCreatingFile()
    {
        // Arrange: 回环地址上两个服务实例
        var tempDir = Path.Combine(Path.GetTempPath(), "fs-textmsg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var directoryService = new Mock<IPlatformDirectoryService>();
        directoryService.Setup(d => d.GetDownloadsDirectory()).Returns(tempDir);
        directoryService.Setup(d => d.GetPicturesDirectory()).Returns(tempDir);
        directoryService.Setup(d => d.GetVideosDirectory()).Returns(tempDir);
        directoryService.Setup(d => d.GetMusicDirectory()).Returns(tempDir);
        directoryService.Setup(d => d.GetDocumentsDirectory()).Returns(tempDir);

        var senderPort = GetFreePort();
        var receiverPort = GetFreePort();
        using var sender = new TcpFileTransferService(directoryService.Object, senderPort);
        using var receiver = new TcpFileTransferService(directoryService.Object, receiverPort);

        const string text = "你好，LocalSend！\n这是第二行 / 带路径字符的文本消息。";
        var received = new TaskCompletionSource<FileTransferInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnTransferCompleted += (info, _) => received.TrySetResult(info);

        var confirmAsked = false;
        receiver.OnTransferRequestSendAndReceive += _ => confirmAsked = true;

        var senderInfo = new TaskCompletionSource<FileTransferInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.OnTransferCompleted += (info, _) => senderInfo.TrySetResult(info);

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
                Port = receiverPort
            };

            // Act
            var success = await sender.SendTextMessageAsync(text, target, "sender-device");

            // Assert
            Assert.True(success);
            var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(confirmAsked, "消息条目也应出现在传输列表里");
            Assert.True(completed.IsMessage);
            Assert.Equal(text, completed.MessageText);
            Assert.Equal(TransferStatus.Completed, completed.Status);
            Assert.Equal(TransferDirection.Receive, completed.Direction);
            Assert.Null(completed.FilePath);
            Assert.Null(completed.SavePath);
            Assert.False(string.IsNullOrWhiteSpace(completed.FileName), "消息应有一个可读摘要");
            Assert.DoesNotContain("/", completed.FileName);
            Assert.DoesNotContain("\\", completed.FileName);
            Assert.Empty(Directory.GetFiles(tempDir)); // 文本消息不应落盘

            var sent = await senderInfo.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(sent.IsMessage);
            Assert.Equal(text, sent.MessageText);
            Assert.Equal(TransferStatus.Completed, sent.Status);
        }
        finally
        {
            await sender.StopAsync();
            await receiver.StopAsync();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// 回归：对端处理完消息就直接断开（或旧版本不认识该请求类型、不回 ACK）时，
    /// 发送端不能因为短包 BitConverter 越界把这次发送写成「失败」。
    /// </summary>
    [Fact]
    public async Task SendTextMessageAsync_WithoutAck_IsStillCompleted()
    {
        var directoryService = new Mock<IPlatformDirectoryService>();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        using var sender = new TcpFileTransferService(directoryService.Object, GetFreePort());
        var status = new TaskCompletionSource<TransferStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.OnTransferCompleted += (info, _) => status.TrySetResult(info.Status);

        // 假对端：读完整的请求帧后立刻关闭连接，不回 ACK
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var header = new byte[4];
            var read = 0;
            while (read < 4)
            {
                var n = await stream.ReadAsync(header, read, 4 - read);
                if (n == 0) break;
                read += n;
            }
            if (read < 4) return;

            var length = BitConverter.ToInt32(header, 0);
            var body = new byte[length];
            read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body, read, length - read);
                if (n == 0) break;
                read += n;
            }
        });

        try
        {
            var target = new DeviceInfo
            {
                DeviceId = "no-ack-peer",
                DeviceName = "NoAck",
                DeviceType = DeviceType.Desktop,
                IpAddress = "127.0.0.1",
                Port = port
            };

            var success = await sender.SendTextMessageAsync("hello", target, "sender-device");
            await server.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(success, "无 ACK 也应视为已送达");
            var result = await status.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TransferStatus.Completed, result);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void FileTransferInfo_MessageFlags_AreIndependentOfFileFields()
    {
        var info = new FileTransferInfo
        {
            TransferId = "t1",
            FileName = "a.txt",
            FileSize = 10,
            SenderId = "s",
            ReceiverId = "r"
        };

        Assert.False(info.IsMessage);
        Assert.Null(info.MessageText);

        info.IsMessage = true;
        info.MessageText = "内容";
        Assert.True(info.IsMessage);
        Assert.Equal("内容", info.MessageText);
    }

    [Fact]
    public async Task Database_MessageColumn_IsMigratedAndPersisted()
    {
        // 这个测试依赖 NuGet 自带的 e_sqlite3，而它需要 glibc 2.28+；
        // Ubuntu 18.04（glibc 2.27）加载不了。应用本身在 Linux 上走的是系统 libsqlite3
        // （见 Program.ExtractAndLoadNativeLibs 的 excludedOnLinux），所以这里跳过不影响产品验证。
        try
        {
            using var probe = new SqliteConnection("Data Source=:memory:");
            probe.Open();
        }
        catch (Exception ex)
        {
            Assert.Skip($"当前环境无法加载 e_sqlite3，跳过数据库测试：{ex.GetBaseException().Message}");
        }

        var dir = Path.Combine(Path.GetTempPath(), "fs-msgdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");

        try
        {
            // 模拟旧版本数据库：没有 Message 列
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS ReceiveHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SenderId TEXT NOT NULL, SenderDeviceName TEXT,
                        FileName TEXT NOT NULL, FileSize INTEGER NOT NULL,
                        SavePath TEXT NOT NULL, CreatedAt DATETIME NOT NULL,
                        Direction INTEGER NOT NULL DEFAULT 0, PeerId TEXT, PeerName TEXT,
                        FilePath TEXT, Status INTEGER NOT NULL DEFAULT 2, ErrorMessage TEXT
                    );
                    INSERT INTO ReceiveHistory (SenderId, FileName, FileSize, SavePath, CreatedAt)
                    VALUES ('old-peer', 'old.txt', 1, '', '2024-01-01');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var service = new DatabaseService(dbPath);

            // 写入一条消息历史（FilePath 为空）
            var ok = await service.AddSingleReceiveHistoryAsync(new ReceiveHistoryEntity
            {
                SenderId = "peer",
                SenderDeviceName = "手机",
                FileName = "你好，这是消息",
                FileSize = 21,
                SavePath = string.Empty,
                Status = TransferStatus.Completed,
                Direction = TransferDirection.Receive,
                CreatedAt = DateTime.UtcNow,
                Message = "你好，这是消息全文\n第二行"
            });

            Assert.True(ok);
            var all = (await service.GetAllReceiveHistoryAsync()).ToList();
            Assert.Equal(2, all.Count);

            var message = Assert.Single(all, x => x.Message is not null);
            Assert.Equal("你好，这是消息全文\n第二行", message.Message);
            Assert.Null(message.FilePath);

            // 老记录迁移后 Message 为 null
            var legacy = Assert.Single(all, x => x.FileName == "old.txt");
            Assert.Null(legacy.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
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
