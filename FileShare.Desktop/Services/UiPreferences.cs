using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileShare.Core.Services;
using FileShare.Desktop.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileShare.Desktop.Services;

// AppSkin / DesktopUiPreferences / IUiPreferencesService 已各拆到独立文件。

/// <summary>
/// 基于 <c>desktop-ui.json</c> 的 UI 偏好服务（与 Core 的 settings.json 分离）。
/// <para>
/// 兼容旧配置：早期版本把语言保存为 <c>"Language": 0|1</c> 枚举值，
/// 首次读取时映射为 BCP-47 字符串（0→zh-CN、1→en-US）并立即写回新格式。
/// </para>
/// </summary>
public sealed class JsonUiPreferencesService : IUiPreferencesService
{
    private const string FileName = "desktop-ui.json";
    private const string DefaultLanguageCode = "zh-CN";

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
    public void SetLanguage(string languageCode)
    {
        lock (_gate)
        {
            _current.LanguageCode = NormalizeCode(languageCode);
        }

        Save();
    }

    /// <inheritdoc />
    public void SetTrayPreferences(bool minimizeToTray, bool closeToTray)
    {
        lock (_gate)
        {
            _current.MinimizeToTray = minimizeToTray;
            _current.CloseToTray = closeToTray;
        }

        Save();
    }

    private static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return DefaultLanguageCode;
        }

        return Loc.Packs.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))
            ? code
            : DefaultLanguageCode;
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

            // 旧格式的 Language 是枚举数字（0=zh-CN / 1=en-US）；source-gen 会忽略该未知字段，
            // 这里用 JsonDocument 单独读出并一次性迁移为 LanguageCode。
            bool migrated = false;
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.TryGetProperty("Language", out var legacy))
                {
                    string? code = legacy.ValueKind switch
                    {
                        JsonValueKind.Number when legacy.TryGetInt32(out var n) => n == 1 ? "en-US" : "zh-CN",
                        JsonValueKind.String => legacy.GetString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(code) &&
                        Loc.Packs.Any(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)))
                    {
                        _current.LanguageCode = code!;
                        migrated = true;
                    }
                }
            }

            _current.LanguageCode = NormalizeCode(_current.LanguageCode);

            if (migrated)
            {
                Save(); // 写回新格式（旧 Language 字段随之消失）
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
                snapshot = new DesktopUiPreferences
                {
                    Skin = _current.Skin,
                    LanguageCode = _current.LanguageCode,
                    MinimizeToTray = _current.MinimizeToTray,
                    CloseToTray = _current.CloseToTray
                };
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
