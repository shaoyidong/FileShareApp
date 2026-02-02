using FileShare.Core.Services;
using Microsoft.Maui.ApplicationModel;

namespace FileShare.Mobile.Services;

#if ANDROID
public class AndroidPermissionService : IPermissionService
{
    public async Task<bool> RequestStoragePermissionAsync()
    {
        try
        {
            var status = await Permissions.RequestAsync<Permissions.StorageRead>();
            if (status != PermissionStatus.Granted)
            {
                return false;
            }
            
            status = await Permissions.RequestAsync<Permissions.StorageWrite>();
            if (status != PermissionStatus.Granted)
            {
                return false;
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public async Task<bool> CheckStoragePermissionAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.StorageRead>();
            if (status != PermissionStatus.Granted)
            {
                return false;
            }
            
            status = await Permissions.CheckStatusAsync<Permissions.StorageWrite>();
            if (status != PermissionStatus.Granted)
            {
                return false;
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public Task<bool> RequestQueryAllPackagesPermissionAsync()
    {
        // QUERY_ALL_PACKAGES权限在Android 11+上需要在AndroidManifest.xml中声明
        // 并且在某些设备上可能需要特殊处理
        // 这里默认返回true，因为我们已经在AndroidManifest.xml中添加了该权限
        return Task.FromResult(true);
    }
    
    public Task<bool> CheckQueryAllPackagesPermissionAsync()
    {
        // 默认返回true，因为我们已经在AndroidManifest.xml中添加了该权限
        return Task.FromResult(true);
    }
}

#endif
