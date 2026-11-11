using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileShare.Core.Services;
using FileShare.Desktop.Helpers;
using FileShare.Desktop.Localization;
using FileShare.Desktop.Models;
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
    private const string DefaultThemeId = "win7";

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
    public void SetSkinId(string skinId)
    {
        lock (_gate)
        {
            _current.SkinId = string.IsNullOrWhiteSpace(skinId) ? "win7" : skinId;
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

    /// <summary>
    /// 把旧版枚举名（Win7 / WinXP / WinXPOlive / 0 / 1 / 2）映射成主题 id；
    /// 其它值原样保留，因为用户可以在 themes/ 里放自定义主题包。
    /// </summary>
    internal static string NormalizeThemeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return DefaultThemeId;
        }

        return id.Trim() switch
        {
            "0" or "Win7" or "win7" => "win7",
            "1" or "WinXP" or "winxp" => "winxp",
            "2" or "WinXPOlive" or "winxp-olive" => "winxp-olive",
            var other => other
        };
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

            // 旧格式把皮肤保存为 "Skin": 0|1|2 枚举值；source-gen 会忽略该未知字段，
            // 这里用 JsonDocument 单独读出并一次性迁移为字符串主题 id。
            bool migrated = false;
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                var hasSkinId = root.TryGetProperty("SkinId", out var skinIdElement) &&
                                skinIdElement.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(skinIdElement.GetString());

                if (!hasSkinId && root.TryGetProperty("Skin", out var legacySkin))
                {
                    string? themeId = legacySkin.ValueKind switch
                    {
                        JsonValueKind.Number when legacySkin.TryGetInt32(out var n) => n switch
                        {
                            1 => "winxp",
                            2 => "winxp-olive",
                            _ => "win7"
                        },
                        JsonValueKind.String => legacySkin.GetString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(themeId))
                    {
                        _current.SkinId = NormalizeThemeId(themeId);
                        migrated = true;
                    }
                }

                _current.SkinId = NormalizeThemeId(_current.SkinId);

                // 旧格式的 Language 是枚举数字（0=zh-CN / 1=en-US），同样一次性迁移为 LanguageCode。
                if (root.TryGetProperty("Language", out var legacy))
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
                    SkinId = _current.SkinId,
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
