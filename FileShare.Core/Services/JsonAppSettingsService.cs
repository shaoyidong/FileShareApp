using FileShare.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace FileShare.Core.Services;

/// <summary>
/// 设置服务：进程内单例，负责读取/保存 <see cref="AppSettings"/>。
/// </summary>
public interface IAppSettingsService
{
    /// <summary>当前设置（只读使用；修改请调用 <see cref="Update"/>）。</summary>
    AppSettings Current { get; }

    /// <summary>设置文件路径。</summary>
    string SettingsFilePath { get; }

    /// <summary>设置变化事件（同一进程内）。</summary>
    event EventHandler? Changed;

    /// <summary>修改设置并立即持久化。</summary>
    void Update(Action<AppSettings> mutate);

    /// <summary>把当前设置写入磁盘。</summary>
    void Save();

    /// <summary>从磁盘重新加载。</summary>
    /// <returns>文件存在且解析成功时返回 true。</returns>
    bool Reload();
}

/// <summary>
/// 基于 JSON 文件的设置服务实现。
/// <para>写入采用「临时文件 + 替换」的方式，避免断电/崩溃导致配置文件半写损坏。</para>
/// </summary>
public sealed class JsonAppSettingsService : IAppSettingsService
{
    private readonly object _gate = new();
    private readonly ILogger<JsonAppSettingsService> _logger;
    private AppSettings _current = new();

    public JsonAppSettingsService(string settingsFilePath, ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException("设置文件路径不能为空", nameof(settingsFilePath));
        }

        SettingsFilePath = settingsFilePath;
        _logger = loggerFactory?.CreateLogger<JsonAppSettingsService>() ?? NullLogger<JsonAppSettingsService>.Instance;
        Reload();
    }

    /// <inheritdoc />
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public AppSettings Current
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
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool Reload()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    _current = new AppSettings();
                    return false;
                }

                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.AppSettings)
                             ?? new AppSettings();
                loaded.Normalize();
                _current = loaded;
                _logger.LogInformation("已加载设置: {Path}", SettingsFilePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取设置失败，使用默认设置: {Path}", SettingsFilePath);
                _current = new AppSettings();
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            mutate(_current);
            _current.Normalize();
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Save()
    {
        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = _current.Clone();
        }

        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, SourceGenerationContext.Default.AppSettings);
            var tempPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败: {Path}", SettingsFilePath);
        }
    }
}
