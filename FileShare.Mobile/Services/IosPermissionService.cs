using FileShare.Core.Services;

using Microsoft.Maui.ApplicationModel;

namespace FileShare.Mobile.Services;

#if IOS
using System.Runtime.Versioning;

public class IosPermissionService : IPermissionService
{
    public async Task<bool> RequestStoragePermissionAsync()
    {
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Photos>();
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
            var status = await Permissions.CheckStatusAsync<Permissions.Photos>();
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
        // iOS 没有直接对应的 QUERY_ALL_PACKAGES 权限
        // 在 iOS 中，应用只能访问自己的沙盒和系统提供的特定 API
        return Task.FromResult(true);
    }
    
    public Task<bool> CheckQueryAllPackagesPermissionAsync()
    {
        // iOS 没有直接对应的 QUERY_ALL_PACKAGES 权限
        return Task.FromResult(true);
    }

    public Task<bool> RequestInstallPackagePermissionAsync()
    {
        // iOS 应用安装权限由系统管理
        // 应用无法直接请求安装其他应用的权限
        // 在 iOS 中，应用只能通过 App Store 安装
        return Task.FromResult(true);
    }

    public Task<bool> CheckInstallPackagePermissionAsync()
    {
        // iOS 应用安装权限由系统管理
        return Task.FromResult(true);
    }
}

#endif