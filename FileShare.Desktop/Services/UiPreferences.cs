using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileShare.Core.Services;
using FileShare.Desktop.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileShare.Desktop.Services;

/// <summary>
/// 桌面端皮肤。**只属于 Desktop**：移动端有自己的视觉体系，不应该被 Win7/XP 皮肤污染，
/// 因此皮肤选择不再放在 Core 的 AppSettings 里，而是由桌面项目单独持久化。
/// </summary>
public enum AppSkin
{
    /// <summary>Windows 7 / Aero（默认）。</summary>
    Win7 = 0,

    /// <summary>Windows XP / Luna Blue（经典蓝）。</summary>
    WinXP = 1,

    /// <summary>Windows XP / Luna Olive（橄榄绿）。</summary>
    WinXPOlive = 2,
}

/// <summary>
/// 桌面端 UI 偏好（目前只有皮肤，后续可扩展字号/密度等纯 UI 配置）。
/// </summary>
public sealed class DesktopUiPreferences
{
    /// <summary>选中皮肤。</summary>
    public AppSkin Skin { get; set; } = AppSkin.Win7;

    /// <summary>界面语言。</summary>
    public AppLanguage Language { get; set; } = AppLanguage.ZhCN;
}

/// <summary>
/// 桌面端 UI 偏好存取。
/// </summary>
public interface IUiPreferencesService
{
    /// <summary>当前偏好。</summary>
    DesktopUiPreferences Current { get; }

    /// <summary>更新皮肤并立即保存。</summary>
    void SetSkin(AppSkin skin);

    /// <summary>更新语言并立即保存。</summary>
    void SetLanguage(AppLanguage language);
}

/// <summary>
/// 基于 <c>desktop-ui.json</c> 的 UI 偏好服务（与 Core 的 settings.json 分离）。
/// </summary>
public sealed class JsonUiPreferencesService : IUiPreferencesService
{
    private const string FileName = "desktop-ui.json";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<JsonUiPreferencesService> _logger;
    private DesktopUiPreferences _current = new();

    public JsonUiPreferencesService(string? dataDirectory = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<JsonUiPreferencesService>() ?? NullLogger<JsonUiPreferencesService>.Instance;

        var directory = string.IsNullOrWhiteSpace(dataDirectory)
            ? AppPaths.GetDataDirectory()
            : dataDirectory!;

        _filePath = Path.Combine(directory, FileName);
        Reload();
    }

    /// <inheritdoc />
    public DesktopUiPreferences Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public void SetSkin(AppSkin skin)
    {
        lock (_gate)
        {
            _current.Skin = skin;
        }

        Save();
    }

    /// <inheritdoc />
    public void SetLanguage(AppLanguage language)
    {
        lock (_gate)
        {
            _current.Language = language;
        }

        Save();
    }

    private void Reload()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize(json, DesktopJsonContext.Default.DesktopUiPreferences);
            if (loaded is not null)
            {
                lock (_gate)
                {
                    _current = loaded;
                }
            }

            if (!Enum.IsDefined(_current.Skin))
            {
                _current.Skin = AppSkin.Win7;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取桌面 UI 偏好失败，使用默认皮肤");
            _current = new DesktopUiPreferences();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            DesktopUiPreferences snapshot;
            lock (_gate)
            {
                snapshot = new DesktopUiPreferences { Skin = _current.Skin, Language = _current.Language };
            }

            var json = JsonSerializer.Serialize(snapshot, DesktopJsonContext.Default.DesktopUiPreferences);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存桌面 UI 偏好失败");
        }
    }
}

/// <summary>桌面端 JSON 源生成上下文（AOT 友好）。</summary>
[JsonSerializable(typeof(DesktopUiPreferences))]
internal partial class DesktopJsonContext : JsonSerializerContext
{
}
