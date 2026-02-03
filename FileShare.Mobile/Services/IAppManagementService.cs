using FileShare.Mobile.Model;

namespace FileShare.Mobile.Services;

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


