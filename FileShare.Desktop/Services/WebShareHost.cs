using FileShare.Core.Services;
using FileShare.Core.Web;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.Services;

/// <summary>
/// 网页分享宿主：根据设置启动/停止 <see cref="WebShareService"/>，并负责 PIN 的生成与持久化。
/// </summary>
public interface IWebShareHost
{
    /// <summary>按当前设置应用（启动或停止）。</summary>
    void Apply();

    /// <summary>当前访问地址（未启用时为 null）。</summary>
    string? Url { get; }

    /// <summary>当前 PIN。</summary>
    string Pin { get; }

    /// <summary>重新生成 PIN 并重启服务。</summary>
    string RegeneratePin();
}

/// <inheritdoc cref="IWebShareHost"/>
public sealed class WebShareHost : IWebShareHost, IDisposable
{
    private readonly IAppSettingsService _settingsService;
    private readonly IPlatformDirectoryService _directoryService;
    private readonly IToastService? _toastService;
    private readonly IDatabaseService? _databaseService;
    private readonly ILogger<WebShareHost> _logger;
    private readonly WebShareService _service;

    public WebShareHost(
        IAppSettingsService settingsService,
        IPlatformDirectoryService directoryService,
        ILoggerFactory loggerFactory,
        IToastService? toastService = null,
        IDatabaseService? databaseService = null)
    {
        _settingsService = settingsService;
        _directoryService = directoryService;
        _databaseService = databaseService;
        _toastService = toastService;
        _logger = loggerFactory.CreateLogger<WebShareHost>();

        _service = new WebShareService(loggerFactory);
        _service.FileReceived += OnFileReceived;
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

        var saveDirectory = string.IsNullOrWhiteSpace(settings.SaveDirectory)
            ? _directoryService.GetDownloadsDirectory()
            : settings.SaveDirectory!;

        try
        {
            _service.Start(new WebShareOptions
            {
                Port = settings.WebSharePort,
                Pin = pin,
                SaveDirectory = saveDirectory
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

    private void OnFileReceived(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;

        // 记录到接收历史（与局域网接收一致，历史里可直接打开/定位）
        _ = RecordHistoryAsync(path, directory);

        _toastService?.Show(
            Loc.Get("网页分享收到文件"),
            Loc.F("{0}（已保存到 {1}）", Path.GetFileName(path), directory),
            null);
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
            long size = 0;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    size = info.Length;
                }
            }
            catch (Exception)
            {
                // 大小只用于展示
            }

            var entity = new Core.Models.Entities.ReceiveHistoryEntity
            {
                SenderId = "web-share",
                SenderDeviceName = "Web Share",
                PeerId = "web-share",
                PeerName = "Web Share",
                FileName = Path.GetFileName(path),
                FileSize = size,
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

    private static string GeneratePin() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    /// <inheritdoc />
    public void Dispose()
    {
        _service.FileReceived -= OnFileReceived;
        _service.Dispose();
    }
}
