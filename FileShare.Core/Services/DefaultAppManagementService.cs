using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Core.Services;

/// <summary>
/// 默认应用管理服务实现（用于非Android平台）
/// </summary>
public class DefaultAppManagementService : IAppManagementService
{
    public Task<List<InstalledAppInfo>> GetInstalledAppsAsync()
    {
        return Task.FromResult(new List<InstalledAppInfo>());
    }
    
    public Task<string?> GetApkFilePathAsync(string packageName)
    {
        return Task.FromResult<string?>(null);
    }
    
    public Task<bool> ExtractApkAsync(string packageName, string destinationPath)
    {
        return Task.FromResult(false);
    }
}
