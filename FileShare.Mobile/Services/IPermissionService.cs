namespace FileShare.Mobile.Services;

/// <summary>
/// 权限管理服务接口
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// 请求存储权限
    /// </summary>
    /// <returns>是否获得权限</returns>
    Task<bool> RequestStoragePermissionAsync();
    
    /// <summary>
    /// 检查是否有存储权限
    /// </summary>
    /// <returns>是否有权限</returns>
    Task<bool> CheckStoragePermissionAsync();
    
    /// <summary>
    /// 请求查询所有应用权限（Android）
    /// </summary>
    /// <returns>是否获得权限</returns>
    Task<bool> RequestQueryAllPackagesPermissionAsync();
    
    /// <summary>
    /// 检查是否有查询所有应用权限（Android）
    /// </summary>
    /// <returns>是否有权限</returns>
    Task<bool> CheckQueryAllPackagesPermissionAsync();

    /// <summary>
    /// 请求安装应用权限
    /// </summary>
    /// <returns></returns>
    Task<bool> RequestInstallPackagePermissionAsync();

    /// <summary>
    /// 检查是否有安装应用权限
    /// </summary>
    /// <returns></returns>
    Task<bool> CheckInstallPackagePermissionAsync();
}
