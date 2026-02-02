using FileShare.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;

public class AndroidAppManagementService : IAppManagementService
{
    private readonly Context? _context;
    private readonly PackageManager _packageManager;
    
    public AndroidAppManagementService()
    {
        _context = Android.App.Application.Context;
        _packageManager = _context?.PackageManager!;
    }
    
    public async Task<List<InstalledAppInfo>> GetInstalledAppsAsync()
    {
        return await Task.Run(() => {
            var apps = new List<InstalledAppInfo>();
            var packages = _packageManager.GetInstalledPackages(PackageInfoFlags.MetaData);
            
            foreach (var packageInfo in packages)
            {
                try
                {
                    var appInfo = _packageManager.GetApplicationInfo(packageInfo?.PackageName!, 0);
                    var appName = _packageManager.GetApplicationLabel(appInfo)?.ToString() ?? packageInfo?.PackageName;
                    var isSystemApp = (appInfo.Flags & ApplicationInfoFlags.System) != 0;
                    
                    // 获取APK文件大小
                    long apkSize = 0;
                    var apkPath = appInfo.SourceDir;
                    if (File.Exists(apkPath))
                    {
                        apkSize = new FileInfo(apkPath).Length;
                    }
                    
                    // 获取应用图标
                    string? iconPath = null;
                    try
                    {
                        var icon = _packageManager.GetApplicationIcon(appInfo);
                        if (icon != null)
                        {
                            // 这里可以添加将Drawable转换为Bitmap并保存为临时文件的逻辑
                            // 由于这需要更多的代码，暂时设置为null
                        }
                    }
                    catch { }
                    
                    apps.Add(new InstalledAppInfo
                    {
                        PackageName = packageInfo?.PackageName!,
                        AppName = appName!,
                        VersionName = packageInfo?.VersionName ?? "",
                        ApkSize = apkSize,
                        IsSystemApp = isSystemApp,
                        IconPath = iconPath
                    });
                }
                catch { }
            }
            
            // 按应用名称排序
            return apps.OrderBy(a => a.AppName).ToList();
        });
    }
    
    public async Task<string?> GetApkFilePathAsync(string packageName)
    {
        return await Task.Run(() => {
            try
            {
                var appInfo = _packageManager.GetApplicationInfo(packageName, 0);
                return appInfo.SourceDir;
            }
            catch
            {
                return null;
            }
        });
    }
    
    public async Task<bool> ExtractApkAsync(string packageName, string destinationPath)
    {
        return await Task.Run(() => {
            try
            {
                var apkPath = GetApkFilePathAsync(packageName).Result;
                if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath))
                {
                    return false;
                }
                
                // 确保目标目录存在
                var destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                
                // 复制APK文件
                File.Copy(apkPath, destinationPath, true);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}

#endif
