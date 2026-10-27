using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Linq;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 断点续传测试：接收端已有部分数据（<c>.part</c>）时，应告知发送端从该偏移继续，
/// 最终文件内容完整且校验通过。
/// </summary>
public class ResumeTransferTests
{
    private readonly Mock<IPlatformDirectoryService> _directoryService;
    private readonly string _root;

    public ResumeTransferTests()
    {
        _directoryService = new Mock<IPlatformDirectoryService>();
        _root = Path.Combine(Path.GetTempPath(), "FileShareResume_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _directoryService.Setup(d => d.GetDownloadsDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetPicturesDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetVideosDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetMusicDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetDocumentsDirectory()).Returns(_root);
    }

    [Fact]
    public async Task Send_WhenPartFileExists_ResumesFromOffset()
    {
        var fileName = "resume.bin";
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        var sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, fileName);
        await File.WriteAllBytesAsync(sourcePath, payload);

        // 模拟上次中断：已收到前 128KB
        var partPath = Path.Combine(_root, fileName + ".part");
        await File.WriteAllBytesAsync(partPath, payload.Take(128 * 1024).ToArray());

        await using var harness = await TransferHarness.CreateAsync(_directoryService.Object, _root);

        var success = await harness.SendAsync(sourcePath);

        Assert.True(success, harness.Diagnostics);

        var finalPath = Path.Combine(_root, fileName);
        Assert.True(File.Exists(finalPath), "续传后的文件应存在: " + harness.Diagnostics);
        Assert.Equal(payload, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(partPath), "完成后不应残留 .part");

        // 接收端只应收到剩余的一半数据（+少量协议开销）
        Assert.True(
            harness.ReceiverBytes < payload.Length,
            $"续传时不应重传全部数据，实际接收 {harness.ReceiverBytes} 字节");
    }

    [Fact]
    public async Task Send_WithoutPartFile_TransfersNormally()
    {
        var fileName = "normal.bin";
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        var sourcePath = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(sourcePath, payload);

        await using var harness = await TransferHarness.CreateAsync(_directoryService.Object, _root);

        Assert.True(await harness.SendAsync(sourcePath), harness.Diagnostics);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_root, fileName)));
        Assert.True(harness.ReceiverBytes >= payload.Length);
    }

    private sealed class TransferHarness : IAsyncDisposable
    {
        private readonly TcpFileTransferService _sender;
        private readonly TcpFileTransferService _receiver;
        private readonly int _port;
        private readonly CollectingLoggerFactory _loggerFactory;
        private long _baselineReceived;

        private TransferHarness(
            TcpFileTransferService sender,
            TcpFileTransferService receiver,
            int port,
            CollectingLoggerFactory loggerFactory,
            long baselineReceived)
        {
            _sender = sender;
            _receiver = receiver;
            _port = port;
            _loggerFactory = loggerFactory;
            _baselineReceived = baselineReceived;
        }

        public List<string> Events { get; } = new();

        public string Diagnostics => string.Join(" | ", _loggerFactory.Lines.Concat(Events));

        public long ReceiverBytes => _receiver.TotalBytesReceived - _baselineReceived;

        public static async Task<TransferHarness> CreateAsync(IPlatformDirectoryService directoryService, string saveDirectory)
        {
            var receiverPort = GetFreePort();
            var senderPort = GetFreePort();

            var loggerFactory = new CollectingLoggerFactory();
            var sender = new TcpFileTransferService("sender-device", directoryService, senderPort, false, loggerFactory);
            var receiver = new TcpFileTransferService("receiver-device", directoryService, receiverPort, false, loggerFactory);

            var harness = new TransferHarness(sender, receiver, receiverPort, loggerFactory, receiver.TotalBytesReceived);

            receiver.OnTransferRequestSendAndReceive += info =>
            {
                harness.Events.Add($"接收请求: {info.FileName}");
                receiver.HandleTransferRequest(info.TransferId, accept: true, savePath: saveDirectory);
            };
            receiver.OnTransferCompleted += (info, message) => harness.Events.Add($"接收完成: {info.Status} {message}");
            sender.OnTransferCompleted += (info, message) => harness.Events.Add($"发送完成: {info.Status} {message}");

            await receiver.StartAsync();
            await sender.StartAsync();

            return harness;
        }

        public Task<bool> SendAsync(string filePath)
        {
            var target = new DeviceInfo
            {
                DeviceId = "receiver-device",
                DeviceName = "Receiver",
                IpAddress = "127.0.0.1",
                Port = _port,
                DeviceType = DeviceType.Desktop
            };

            return _sender.SendFileAsync(filePath, target, "sender-device", null, null);
        }

        public async ValueTask DisposeAsync()
        {
            await _sender.StopAsync();
            await _receiver.StopAsync();
            _sender.Dispose();
            _receiver.Dispose();
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

    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = new();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<string> _lines;

            public CollectingLogger(string category, List<string> lines)
            {
                _category = category;
                _lines = lines;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_lines)
                {
                    _lines.Add($"[{logLevel}] {_category}: {formatter(state, exception)} {exception?.Message}");
                }
            }
        }
    }
}
