using FileShare.Core.Services;
using FileShare.Mobile.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace FileShare.Mobile.ViewModels;

public class AppListViewModel : ViewModelBase
{
    private readonly IAppManagementService _appManagementService;
    private readonly IFileShareServiceManager _fileShareService;
    private readonly IAlertService _alertService;
    private readonly IPermissionService _permissionService;
    
    public ObservableCollection<InstalledAppInfo> Apps { get; } = new();
    
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }
    
    public ICommand SendAppCommand { get; }
    public ICommand RefreshCommand { get; }
    
    public AppListViewModel(IAppManagementService appManagementService, IFileShareServiceManager fileShareService, IAlertService alertService, IPermissionService permissionService)
    {
        _appManagementService = appManagementService;
        _fileShareService = fileShareService;
        _alertService = alertService;
        _permissionService = permissionService;
        //SendAppCommand = new Command<InstalledAppInfo>(async (app) => await SendAppAsync(app));
        RefreshCommand = new Command(async () => await LoadAppsAsync());
    }
    
    public async Task LoadAppsAsync()
    {
        IsLoading = true;
        try
        {
            // 检查并请求必要的权限
            var hasStoragePermission = await _permissionService.RequestStoragePermissionAsync();
            var hasQueryPackagesPermission = await _permissionService.RequestQueryAllPackagesPermissionAsync();
            
            if (!hasStoragePermission)
            {
                await _alertService.DisplayToastAsync("权限不足, 需要存储权限来提取和发送APK文件");
                return;
            }
            
            if (!hasQueryPackagesPermission)
            {
                await _alertService.DisplayToastAsync("权限不足, 需要查询所有应用的权限来获取已安装应用列表");
                return;
            }
            
            var apps = (await _appManagementService.GetInstalledAppsAsync()).Where(a=>!a.IsSystemApp);
            Apps.Clear();
            foreach (var app in apps)
            {
                Apps.Add(app);
            }
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync("错误, 加载应用列表失败: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    //public async Task SendAppAsync(InstalledAppInfo app)
    //{
    //    try
    //    {
    //        // 创建临时目录
    //        var tempDir = Path.Combine(FileSystem.CacheDirectory, "temp_apks");
    //        Directory.CreateDirectory(tempDir);
            
    //        // 构建目标文件路径
    //        var apkFileName = $"{app.AppName}_{app.VersionName}.apk";
    //        var destinationPath = Path.Combine(tempDir, apkFileName);
            
    //        // 提取APK文件
    //        var success = await _appManagementService.ExtractApkAsync(app.PackageName, destinationPath);
            
    //        if (success && File.Exists(destinationPath))
    //        {
    //            // 使用现有的文件共享机制发送文件
    //            await _fileShareService.SendFileAsync(destinationPath);
    //        }
    //        else
    //        {
    //            await _alertService.ShowAlertAsync("错误", "提取APK文件失败");
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        await _alertService.ShowAlertAsync("错误", "发送应用失败: " + ex.Message);
    //    }
    //}
}
