using FileShare.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileShare.Mobile.Services;

#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using FileShare.Mobile.Model;

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
                    ImageSource? imageSource = null;
                    try
                    {
                        var drawable = _packageManager.GetApplicationIcon(appInfo);
                        // 将 Drawable 转换为 Bitmap
                        if (drawable is BitmapDrawable bitmapDrawable)
                        {
                            var bitmap = bitmapDrawable.Bitmap;
                            imageSource = ConvertBitmapToImageSource(bitmap!);
                        }
                        else
                        {
                            // 处理其他类型的 Drawable
                            var bitmap = Bitmap.CreateBitmap(
                                Math.Min(drawable.IntrinsicWidth,64),
                                Math.Min(drawable.IntrinsicHeight,64),
                                Bitmap.Config.Argb8888!);

                            var canvas = new Canvas(bitmap);
                            drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
                            drawable.Draw(canvas);

                            imageSource = ConvertBitmapToImageSource(bitmap);
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
                        Icon = imageSource
                    });
                }
                catch { }
            }
            
            // 按应用名称排序
            return apps.OrderBy(a => a.AppName).ToList();
        });
    }

    private ImageSource ConvertBitmapToImageSource(Bitmap bitmap)
    {
        // 保存 bitmap 数据到 byte[]
        byte[] imageData;
        using (var stream = new MemoryStream())
        {
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
            imageData = stream.ToArray();
        }

        // 回收 Bitmap 内存
        if (!bitmap.IsRecycled)
        {
            bitmap.Recycle();
        }
        
        // 在委托内部创建新的 MemoryStream
        return ImageSource.FromStream(() => new MemoryStream(imageData));

        // 方法2：使用 PlatformImage（需要 Microsoft.Maui.Graphics）
        // return bitmap.ToImageSource();
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
                //var destDir = Path.GetDirectoryName(destinationPath);
                //if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                //{
                //    Directory.CreateDirectory(destDir);
                //}
                
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
