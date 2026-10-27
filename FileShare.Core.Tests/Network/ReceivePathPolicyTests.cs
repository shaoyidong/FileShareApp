using FileShare.Core.Models;
using FileShare.Core.Network;
using FileShare.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Net.Sockets;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 接收端落盘策略测试：同名文件不覆盖、文件夹传输保留目录结构、目录穿越被拒绝。
/// </summary>
public class ReceivePathPolicyTests
{
    private readonly Mock<IPlatformDirectoryService> _directoryService;
    private readonly string _root;

    public ReceivePathPolicyTests()
    {
        _directoryService = new Mock<IPlatformDirectoryService>();
        _root = Path.Combine(Path.GetTempPath(), "FileSharePathPolicy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _directoryService.Setup(d => d.GetDownloadsDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetPicturesDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetVideosDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetMusicDirectory()).Returns(_root);
        _directoryService.Setup(d => d.GetDocumentsDirectory()).Returns(_root);
    }

    [Fact]
    public async Task SendSameFileTwice_DefaultPolicy_KeepsBothFiles()
    {
        var source = Path.Combine(_root, "source", "报告.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "hello");

        await using var harness = await TransferHarness.CreateAsync(_directoryService.Object, _root, FileNameConflictPolicy.Rename);

        Assert.True(await harness.SendAsync(source), harness.Diagnostics);
        Assert.True(await harness.SendAsync(source), harness.Diagnostics);

        Assert.True(File.Exists(Path.Combine(_root, "报告.txt")), harness.Diagnostics);
        Assert.True(File.Exists(Path.Combine(_root, "报告 (1).txt")), harness.Diagnostics);
    }

    [Fact]
    public async Task SendSameFileTwice_OverwritePolicy_ReplacesFile()
    {
        var source = Path.Combine(_root, "source2", "over.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "v1");

        await using var harness = await TransferHarness.CreateAsync(_directoryService.Object, _root, FileNameConflictPolicy.Overwrite);

        Assert.True(await harness.SendAsync(source), harness.Diagnostics);
        await File.WriteAllTextAsync(source, "v2");
        Assert.True(await harness.SendAsync(source), harness.Diagnostics);

        Assert.False(File.Exists(Path.Combine(_root, "over (1).txt")), harness.Diagnostics);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(_root, "over.txt")));
    }

    [Fact]
    public async Task SendWithRelativePath_CreatesNestedDirectories()
    {
        var source = Path.Combine(_root, "source3", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });

        await using var harness = await TransferHarness.CreateAsync(_directoryService.Object, _root, FileNameConflictPolicy.Rename);

        Assert.True(
            await harness.SendAsync(source, Path.Combine("相册", "2024", "photo.jpg")),
            harness.Diagnostics);

        Assert.True(File.Exists(Path.Combine(_root, "相册", "2024", "photo.jpg")), harness.Diagnostics);
    }

    [Fact]
    public async Task Send_WithDotDotRelativePath_IsRejectedByReceiver()
    {
        var source = Path.Combine(_root, "source4", "evil.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "evil");

        await using var harness = await TransferHarness.CreateAsync(_directoryService.Object, _root, FileNameConflictPolicy.Rename);

        var success = await harness.SendAsync(source, "../escaped.txt");

        var escaped = Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt");
        Assert.False(File.Exists(escaped), "目录穿越写入必须被拒绝: " + harness.Diagnostics);

        // 接收端拒绝请求，发送方应得到失败
        Assert.False(success, harness.Diagnostics);
    }

    /// <summary>把收发两端 + 自动接受装配成一个测试夹具。</summary>
    private sealed class TransferHarness : IAsyncDisposable
    {
        private readonly TcpFileTransferService _sender;
        private readonly TcpFileTransferService _receiver;
        private readonly int _port;
        private readonly CollectingLoggerFactory _loggerFactory;

        private TransferHarness(
            TcpFileTransferService sender,
            TcpFileTransferService receiver,
            int port,
            CollectingLoggerFactory loggerFactory)
        {
            _sender = sender;
            _receiver = receiver;
            _port = port;
            _loggerFactory = loggerFactory;
        }

        public List<string> Events { get; } = new();

        public string Diagnostics => string.Join(" | ", _loggerFactory.Lines.Concat(Events));

        public static async Task<TransferHarness> CreateAsync(
            IPlatformDirectoryService directoryService,
            string saveDirectory,
            FileNameConflictPolicy policy)
        {
            var receiverPort = GetFreePort();
            var senderPort = GetFreePort();

            var loggerFactory = new CollectingLoggerFactory();
            var sender = new TcpFileTransferService("sender-device", directoryService, senderPort, false, loggerFactory, policy);
            var receiver = new TcpFileTransferService("receiver-device", directoryService, receiverPort, false, loggerFactory, policy);

            var harness = new TransferHarness(sender, receiver, receiverPort, loggerFactory);

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

        public Task<bool> SendAsync(string filePath, string? relativePath = null)
        {
            var target = new DeviceInfo
            {
                DeviceId = "receiver-device",
                DeviceName = "Receiver",
                IpAddress = "127.0.0.1",
                Port = _port,
                DeviceType = DeviceType.Desktop
            };

            return _sender.SendFileAsync(filePath, target, "sender-device", relativePath);
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
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    /// <summary>收集日志，失败时一并输出，便于定位。</summary>
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
