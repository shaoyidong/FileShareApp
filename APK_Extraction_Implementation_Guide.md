# APK提取和发送功能实现指南

## 功能概述

本指南将指导您如何在 Mobile 项目中实现类似 LocalSend 的功能，即从已安装应用生成 APK 文件并发送。

## 实现原理

### LocalSend 的实现机制

LocalSend 在 Android 平台上实现 APK 提取和发送的核心原理是：

1. **获取已安装应用列表**：使用 `PackageManager` 获取设备上所有已安装的应用信息
2. **获取 APK 文件路径**：通过 `ApplicationInfo.sourceDir` 获取应用的 APK 文件在设备上的存储路径
3. **提取和复制 APK**：将 APK 文件复制到临时目录，然后通过现有的文件共享机制发送

### Android 系统 API

关键 API 和类：
- `PackageManager`：管理应用包的类，用于获取已安装应用信息
- `ApplicationInfo`：包含应用信息的类，其中 `sourceDir` 属性指向 APK 文件路径
- `PackageInfo`：包含应用包信息的类，如版本号等

## 已完成的实现

### 核心服务

1. **IAppManagementService 接口**：定义了应用管理的核心功能
   - `GetInstalledAppsAsync()`：获取已安装应用列表
   - `GetApkFilePathAsync()`：获取应用的 APK 文件路径
   - `ExtractApkAsync()`：提取应用的 APK 文件到指定位置

2. **AndroidAppManagementService 实现**：Android 平台的具体实现
   - 使用 `PackageManager` 获取应用信息
   - 通过 `ApplicationInfo.sourceDir` 获取 APK 路径
   - 实现 APK 文件复制功能

3. **DefaultAppManagementService 实现**：非 Android 平台的默认实现

### 服务注册

在 `MauiProgram.cs` 中已经注册了相应的服务：
- Android 平台：注册 `AndroidAppManagementService`
- 其他平台：注册 `DefaultAppManagementService`

## 完整实现步骤

### 步骤 1：添加必要的权限

在 `AndroidManifest.xml` 中添加以下权限：

```xml
<!-- 读取已安装应用信息 -->
<uses-permission android:name="android.permission.QUERY_ALL_PACKAGES" />
<!-- 访问存储 -->
<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
```

### 步骤 2：创建应用列表页面

创建一个页面用于显示已安装应用列表，用户可以从中选择要发送的应用。

#### 2.1 创建应用列表视图模型

```csharp
using FileShare.Core.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace FileShare.Mobile.ViewModels;

public class AppListViewModel : ViewModelBase
{
    private readonly IAppManagementService _appManagementService;
    private readonly IFileShareServiceManager _fileShareService;
    
    public ObservableCollection<InstalledAppInfo> Apps { get; } = new();
    public bool IsLoading { get; set; }
    
    public AppListViewModel(IAppManagementService appManagementService, IFileShareServiceManager fileShareService)
    {
        _appManagementService = appManagementService;
        _fileShareService = fileShareService;
    }
    
    public async Task LoadAppsAsync()
    {
        IsLoading = true;
        try
        {
            var apps = await _appManagementService.GetInstalledAppsAsync();
            Apps.Clear();
            foreach (var app in apps)
            {
                Apps.Add(app);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    public async Task SendAppAsync(InstalledAppInfo app)
    {
        // 创建临时目录
        var tempDir = Path.Combine(FileSystem.CacheDirectory, "temp_apks");
        Directory.CreateDirectory(tempDir);
        
        // 构建目标文件路径
        var apkFileName = $"{app.AppName}_{app.VersionName}.apk";
        var destinationPath = Path.Combine(tempDir, apkFileName);
        
        // 提取APK文件
        var success = await _appManagementService.ExtractApkAsync(app.PackageName, destinationPath);
        
        if (success && File.Exists(destinationPath))
        {
            // 使用现有的文件共享机制发送文件
            await _fileShareService.SendFileAsync(destinationPath);
        }
    }
}
```

#### 2.2 创建应用列表页面

```xaml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:viewmodels="clr-namespace:FileShare.Mobile.ViewModels"
             x:Class="FileShare.Mobile.Views.AppListPage"
             Title="已安装应用">
    
    <ContentPage.BindingContext>
        <viewmodels:AppListViewModel />
    </ContentPage.BindingContext>
    
    <StackLayout>
        <ActivityIndicator IsRunning="{Binding IsLoading}" />
        <ListView ItemsSource="{Binding Apps}" HasUnevenRows="True">
            <ListView.ItemTemplate>
                <DataTemplate>
                    <ViewCell>
                        <StackLayout Padding="10">
                            <Label Text="{Binding AppName}" FontAttributes="Bold" />
                            <Label Text="{Binding PackageName}" FontSize="Small" TextColor="Gray" />
                            <Label Text="版本: {Binding VersionName}" FontSize="Small" TextColor="Gray" />
                            <Label Text="大小: {Binding ApkSize, StringFormat='{0:N0} bytes'}" FontSize="Small" TextColor="Gray" />
                            <Button Text="发送 APK" Command="{Binding Source={RelativeSource AncestorType={x:Type viewmodels:AppListViewModel}}, Path=SendAppCommand}" CommandParameter="{Binding}" />
                        </StackLayout>
                    </ViewCell>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </StackLayout>
</ContentPage>
```

### 步骤 3：更新主页面

在主页面添加一个导航到应用列表页面的按钮：

```xaml
<Button Text="发送已安装应用" Command="{Binding NavigateToAppListCommand}" />
```

#### 3.1 更新主页面视图模型

```csharp
using FileShare.Core.Services;
using FileShare.Mobile.Views;
using System.Windows.Input;

namespace FileShare.Mobile.ViewModels;

public class MainPageViewModel : ViewModelBase
{
    private readonly INavigation _navigation;
    private readonly IAppManagementService _appManagementService;
    
    public ICommand NavigateToAppListCommand { get; }
    
    public MainPageViewModel(INavigation navigation, IAppManagementService appManagementService)
    {
        _navigation = navigation;
        _appManagementService = appManagementService;
        NavigateToAppListCommand = new Command(async () => await NavigateToAppList());
    }
    
    private async Task NavigateToAppList()
    {
        await _navigation.PushAsync(new AppListPage());
    }
}
```

### 步骤 4：处理运行时权限

在 Android 6.0+ 上，需要在运行时请求权限：

```csharp
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Permissions;

public async Task RequestPermissionsAsync()
{
    // 请求存储权限
    var status = await Permissions.RequestAsync<Permissions.StorageRead>();
    if (status != PermissionStatus.Granted)
    {
        // 权限被拒绝
        return;
    }
    
    status = await Permissions.RequestAsync<Permissions.StorageWrite>();
    if (status != PermissionStatus.Granted)
    {
        // 权限被拒绝
        return;
    }
    
    // 权限获取成功，可以继续操作
}
```

### 步骤 5：优化和错误处理

1. **错误处理**：在提取和发送过程中添加适当的错误处理
2. **进度显示**：对于大型 APK 文件，添加提取和发送进度显示
3. **缓存管理**：定期清理临时目录中的 APK 文件
4. **过滤系统应用**：可以选择是否显示系统应用
5. **排序和搜索**：添加应用排序和搜索功能

## 完整代码示例

### AndroidAppManagementService 完整实现

```csharp
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
    private readonly Context _context;
    private readonly PackageManager _packageManager;
    
    public AndroidAppManagementService()
    {
        _context = Android.App.Application.Context;
        _packageManager = _context.PackageManager;
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
                    var appInfo = _packageManager.GetApplicationInfo(packageInfo.PackageName, 0);
                    var appName = _packageManager.GetApplicationLabel(appInfo)?.ToString() ?? packageInfo.PackageName;
                    var isSystemApp = (appInfo.Flags & ApplicationInfoFlags.System) != 0;
                    
                    // 获取APK文件大小
                    long apkSize = 0;
                    var apkPath = appInfo.SourceDir;
                    if (File.Exists(apkPath))
                    {
                        apkSize = new FileInfo(apkPath).Length;
                    }
                    
                    apps.Add(new InstalledAppInfo
                    {
                        PackageName = packageInfo.PackageName,
                        AppName = appName,
                        VersionName = packageInfo.VersionName ?? "",
                        ApkSize = apkSize,
                        IsSystemApp = isSystemApp
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
```

## 权限配置

### AndroidManifest.xml 更新

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application android:allowBackup="true" android:icon="@mipmap/appicon" android:roundIcon="@mipmap/appicon_round" android:supportsRtl="true"></application>
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
    <uses-permission android:name="android.permission.INTERNET" />
    <uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />
    <uses-permission android:name="android.permission.QUERY_ALL_PACKAGES" />
    <uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
    <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
</manifest>
```

## 测试和调试

### 测试步骤

1. **构建和部署**：构建应用并部署到 Android 设备
2. **权限请求**：首次运行时，确保应用请求并获得必要的权限
3. **应用列表**：导航到应用列表页面，检查是否正确显示已安装应用
4. **APK 提取**：选择一个应用，点击"发送 APK"按钮
5. **文件发送**：检查 APK 文件是否成功提取并发送

### 常见问题

1. **权限被拒绝**：确保在运行时正确请求权限
2. **APK 提取失败**：检查应用是否有读取 APK 文件的权限
3. **文件过大**：对于大型 APK 文件，可能需要调整文件传输的缓冲区大小
4. **系统应用**：某些系统应用可能无法提取 APK 文件

## 总结

通过本指南的实现，您的应用将具备类似 LocalSend 的功能，可以：

1. **列出已安装应用**：显示设备上所有已安装的应用及其详细信息
2. **提取 APK 文件**：从已安装应用中提取 APK 文件到临时目录
3. **发送 APK 文件**：通过现有的文件共享机制发送提取的 APK 文件

这种实现方式利用了 Android 系统的标准 API，不需要 root 权限，适用于大多数 Android 设备。

## 参考资料

- [Android PackageManager 文档](https://developer.android.com/reference/android/content/pm/PackageManager)
- [Android ApplicationInfo 文档](https://developer.android.com/reference/android/content/pm/ApplicationInfo)
- [LocalSend GitHub 仓库](https://github.com/localsend/localsend)
