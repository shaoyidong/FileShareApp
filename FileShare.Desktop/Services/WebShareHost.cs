using FileShare.Core.Services;
using FileShare.Core.Web;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography;
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
    private readonly ILogger<WebShareHost> _logger;
    private readonly WebShareService _service;

    public WebShareHost(
        IAppSettingsService settingsService,
        IPlatformDirectoryService directoryService,
        ILoggerFactory loggerFactory,
        IToastService? toastService = null)
    {
        _settingsService = settingsService;
        _directoryService = directoryService;
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
        _toastService?.Show(
            Loc.Get("网页分享收到文件"),
            Loc.F("{0}（已保存到 {1}）", Path.GetFileName(path), Path.GetDirectoryName(path)),
            null);
    }

    private static string GeneratePin() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

    /// <inheritdoc />
    public void Dispose()
    {
        _service.FileReceived -= OnFileReceived;
        _service.Dispose();
    }
}
