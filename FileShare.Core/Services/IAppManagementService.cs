namespace FileShare.Core.Services;

/// <summary>
/// 应用管理服务接口
/// </summary>
public interface IAppManagementService
{
    /// <summary>
    /// 获取已安装应用列表
    /// </summary>
    Task<List<InstalledAppInfo>> GetInstalledAppsAsync();
    
    /// <summary>
    /// 获取应用的APK文件路径
    /// </summary>
    /// <param name="packageName">应用包名</param>
    /// <returns>APK文件路径</returns>
    Task<string?> GetApkFilePathAsync(string packageName);
    
    /// <summary>
    /// 提取应用的APK文件到指定位置
    /// </summary>
    /// <param name="packageName">应用包名</param>
    /// <param name="destinationPath">目标保存路径</param>
    /// <returns>提取是否成功</returns>
    Task<bool> ExtractApkAsync(string packageName, string destinationPath);
}

/// <summary>
/// 已安装应用信息
/// </summary>
public class InstalledAppInfo
{
    /// <summary>
    /// 应用包名
    /// </summary>
    public string PackageName { get; set; } = string.Empty;
    
    /// <summary>
    /// 应用名称
    /// </summary>
    public string AppName { get; set; } = string.Empty;
    
    /// <summary>
    /// 应用版本
    /// </summary>
    public string VersionName { get; set; } = string.Empty;
    
    /// <summary>
    /// 应用图标路径
    /// </summary>
    public string? IconPath { get; set; }
    
    /// <summary>
    /// APK文件大小
    /// </summary>
    public long ApkSize { get; set; }
    
    /// <summary>
    /// 是否为系统应用
    /// </summary>
    public bool IsSystemApp { get; set; }
}
