using FileShare.Core.Services;
using FileShare.Mobile.Models;
using FileShare.Mobile.Services;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging.Messages;
using FileShare.Mobile.Messages;

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
    
    private InstalledAppInfo? _selectedApp;
    public InstalledAppInfo? SelectedApp
    {
        get => _selectedApp;
        set 
        {
            if (SetProperty(ref _selectedApp, value) && value != null)
            {
                SelectAppCommand.Execute(value);
            }
        }
    }
    public ICommand RefreshCommand { get; }
    public ICommand SelectAppCommand { get; }
        
    public AppListViewModel(IAppManagementService appManagementService, IFileShareServiceManager fileShareService, IAlertService alertService, IPermissionService permissionService)
    {
        _appManagementService = appManagementService;
        _fileShareService = fileShareService;
        _alertService = alertService;
        _permissionService = permissionService;
        RefreshCommand = new Command(async () => await LoadAppsAsync());
        SelectAppCommand = new Command<InstalledAppInfo>(async (app) => await SelectAppAsync(app));
    }
    
    public async Task SelectAppAsync(InstalledAppInfo app)
    {
        try
        {
            var apkPath = await _appManagementService.GetApkFilePathAsync(app.PackageName);
            if (!string.IsNullOrEmpty(apkPath))
            {
                // 使用WeakReferenceMessenger发送APK路径回MainPageViewModel
                WeakReferenceMessenger.Default.Send(new AppSelectedMessage(apkPath));
                
                // 返回上一页
                await Shell.Current.GoToAsync("..");
            }
            else
            {
                await _alertService.DisplayToastAsync("错误, 提取APK文件失败");
            }
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync("错误, 选择应用失败: " + ex.Message);
        }
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
}
