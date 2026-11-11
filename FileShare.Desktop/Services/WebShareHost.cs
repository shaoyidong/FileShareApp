using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Core.Web;
using FileShare.LocalSend;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.Services;

/// <inheritdoc cref="IWebShareHost"/>
public sealed class WebShareHost : IWebShareHost, IDisposable
{
    private readonly IAppSettingsService _settingsService;
    private readonly IPlatformDirectoryService _directoryService;
    private readonly IToastService? _toastService;
    private readonly IDatabaseService? _databaseService;
    private readonly IAppUiBridge? _uiBridge;
    private readonly ILogger<WebShareHost> _logger;
    private readonly WebShareService _service;
    private string _saveDirectory = string.Empty;

    public WebShareHost(
        IAppSettingsService settingsService,
        IPlatformDirectoryService directoryService,
        ILoggerFactory loggerFactory,
        IToastService? toastService = null,
        IDatabaseService? databaseService = null,
        IAppUiBridge? uiBridge = null)
    {
        _settingsService = settingsService;
        _directoryService = directoryService;
        _databaseService = databaseService;
        _toastService = toastService;
        _uiBridge = uiBridge;
        _logger = loggerFactory.CreateLogger<WebShareHost>();

        _service = new WebShareService(loggerFactory);
        _service.FileReceived += OnFileReceived;
        _service.FileReceiveFailed += OnFileReceiveFailed;
    }

    /// <inheritdoc />
    public string? Url => _service.Url;

    /// <inheritdoc />
    public string Pin => _service.Pin;

    /// <inheritdoc />
    public void Apply()
    {
        var settings = _settingsService.Current;

        if (!settings.WebShareEnabled)
        {
            _service.Stop();
            return;
        }

        // 首次启用时生成 6 位 PIN 并持久化
        var pin = settings.WebSharePin;
        if (string.IsNullOrWhiteSpace(pin))
        {
            pin = GeneratePin();
            _settingsService.Update(s => s.WebSharePin = pin);
        }

        _saveDirectory = string.IsNullOrWhiteSpace(settings.SaveDirectory)
            ? _directoryService.GetDownloadsDirectory()
            : settings.SaveDirectory!;

        try
        {
            _service.Start(new WebShareOptions
            {
                Port = settings.WebSharePort,
                Pin = pin,
                SaveDirectory = _saveDirectory,
                // 与 FileShare / LocalSend 一致：非收藏来源需确认后才落盘，并进入传输列表
                ReceiveApprovalAsync = ApproveUploadAsync
            });

            _logger.LogInformation("网页分享已启用: {Url}", _service.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动网页分享失败（端口 {Port} 可能被占用）", settings.WebSharePort);
            _toastService?.Show(Loc.Get("网页分享启动失败"), ex.Message);
        }
    }

    /// <inheritdoc />
    public string RegeneratePin()
    {
        var pin = GeneratePin();
        _settingsService.Update(settings => settings.WebSharePin = pin);
        Apply();
        return pin;
    }

    /// <summary>上传审批：先在传输列表显示等待确认，再询问用户；拒绝返回 null。</summary>
    private async Task<string?> ApproveUploadAsync(string fileName, long size)
    {
        var transferId = "web:" + Guid.NewGuid().ToString("N");
        if (_uiBridge is null)
        {
            // 无界面宿主（单元测试）：保持原有直接接收行为
            return transferId;
        }

        _uiBridge.ReportTransfer(ExternalTransferUpdate.For(transferId, fileName, size,
            TransferDirection.Receive, TransferStatus.Pending, senderId: "web-share"));

        try
        {
            var decision = await _uiBridge.ConfirmReceiveAsync(new ExternalReceiveRequest(
                "web-share", "网页分享", fileName, size, _saveDirectory)).ConfigureAwait(false);

            if (!decision.Accept)
            {
                _uiBridge.ReportTransfer(ExternalTransferUpdate.For(transferId, fileName, size,
                    TransferDirection.Receive, TransferStatus.Cancelled, senderId: "web-share"));
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "网页分享接收确认失败: {File}", fileName);
            _uiBridge.ReportTransfer(ExternalTransferUpdate.For(transferId, fileName, size,
                TransferDirection.Receive, TransferStatus.Failed, senderId: "web-share"));
            return null;
        }

        _uiBridge.ReportTransfer(ExternalTransferUpdate.For(transferId, fileName, size,
            TransferDirection.Receive, TransferStatus.Transferring, senderId: "web-share"));
        return transferId;
    }

    private void OnFileReceived(string path, string? transferId)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var size = GetFileSize(path);

        if (!string.IsNullOrEmpty(transferId))
        {
            _uiBridge?.ReportTransfer(ExternalTransferUpdate.For(transferId, Path.GetFileName(path), size,
                TransferDirection.Receive, TransferStatus.Completed, size, "web-share", path));
        }

        // 记录到接收历史（与局域网接收一致，历史里可直接打开/定位）
        _ = RecordHistoryAsync(path, directory);

        _toastService?.Show(
            Loc.Get("网页分享收到文件"),
            Loc.F("{0}（已保存到 {1}）", Path.GetFileName(path), directory),
            null);
    }

    private void OnFileReceiveFailed(string path, string? transferId)
    {
        if (string.IsNullOrEmpty(transferId))
        {
            return;
        }

        _uiBridge?.ReportTransfer(ExternalTransferUpdate.For(transferId, Path.GetFileName(path),
            GetFileSize(path), TransferDirection.Receive, TransferStatus.Failed, senderId: "web-share"));
    }

    /// <summary>把网页分享收到的文件写入接收历史。</summary>
    private async Task RecordHistoryAsync(string path, string directory)
    {
        if (_databaseService is null)
        {
            return;
        }

        try
        {
            var entity = new Core.Models.Entities.ReceiveHistoryEntity
            {
                SenderId = "web-share",
                SenderDeviceName = "Web Share",
                PeerId = "web-share",
                PeerName = "Web Share",
                FileName = Path.GetFileName(path),
                FileSize = GetFileSize(path),
                SavePath = directory,
                FilePath = path,
                Status = Core.Models.TransferStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                Direction = Core.Models.TransferDirection.Receive
            };

            await _databaseService.AddSingleReceiveHistoryAsync(entity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入网页分享接收历史失败: {Path}", path);
        }
    }

    private static long GetFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string GeneratePin() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    /// <inheritdoc />
    public void Dispose()
    {
        _service.FileReceived -= OnFileReceived;
        _service.FileReceiveFailed -= OnFileReceiveFailed;
        _service.Dispose();
    }
}
