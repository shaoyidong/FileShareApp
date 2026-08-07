namespace FileShare.Mobile.Services;

/// <summary>
/// 默认权限管理服务实现
/// </summary>
public class DefaultPermissionService : IPermissionService
{
    public Task<bool> RequestStoragePermissionAsync()
    {
        // 默认实现：返回true，假设有权限
        return Task.FromResult(true);
    }
    
    public Task<bool> CheckStoragePermissionAsync()
    {
        // 默认实现：返回true，假设有权限
        return Task.FromResult(true);
    }
    
    public Task<bool> RequestQueryAllPackagesPermissionAsync()
    {
        // 默认实现：返回true，假设有权限
        return Task.FromResult(true);
    }
    
    public Task<bool> CheckQueryAllPackagesPermissionAsync()
    {
        // 默认实现：返回true，假设有权限
        return Task.FromResult(true);
    }

    public Task<bool> RequestInstallPackagePermissionAsync()
    {
        // 默认实现：返回true，假设有权限
        return Task.FromResult(true);
    }

    public Task<bool> CheckInstallPackagePermissionAsync()
    {
        // 默认实现：返回true，假设有权限
        return Task.FromResult(true);
    }
}
