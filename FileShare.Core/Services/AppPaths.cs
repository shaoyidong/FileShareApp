using System.Reflection;

namespace FileShare.Core.Services;

/// <summary>
/// 应用数据目录解析。
/// <para>默认放在 <c>%APPDATA%\{应用名}</c>；如果设置了环境变量
/// <c>FILESHARE_DATA_DIR</c>，则使用该目录（便携模式：把数据放在程序目录/移动盘内，
/// 也便于自动化测试与多实例隔离）。</para>
/// </summary>
public static class AppPaths
{
    /// <summary>覆盖数据目录的环境变量名。</summary>
    public const string DataDirectoryVariable = "FILESHARE_DATA_DIR";

    /// <summary>
    /// 获取数据根目录（配置、数据库、日志、证书都在其下）。
    /// </summary>
    /// <param name="appName">应用名，默认取入口程序集名。</param>
    public static string GetDataDirectory(string? appName = null)
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden.Trim());
        }

        var name = string.IsNullOrWhiteSpace(appName)
            ? Assembly.GetEntryAssembly()?.GetName().Name ?? "FileShare"
            : appName;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, name);
    }

    /// <summary>证书与指纹信任库目录。</summary>
    public static string GetCryptoDirectory(string? appName = null)
    {
        return Path.Combine(GetDataDirectory(appName), "crypto");
    }

    /// <summary>设置文件路径。</summary>
    public static string GetSettingsFilePath(string? appName = null)
    {
        return Path.Combine(GetDataDirectory(appName), "settings.json");
    }

    /// <summary>SQLite 数据库路径。</summary>
    public static string GetDatabaseFilePath(string? appName = null)
    {
        return Path.Combine(GetDataDirectory(appName), "fileshare.db");
    }

    /// <summary>日志目录。</summary>
    public static string GetLogDirectory(string? appName = null)
    {
        return Path.Combine(GetDataDirectory(appName), "logs");
    }
}
