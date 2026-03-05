using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using FileShare.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileShare.Mobile.ViewModels;

public class HistoryViewModel : ViewModelBase
{
    private readonly IFileShareServiceManager _serviceManager;
    private readonly IAlertService _alertService;
    private readonly INavigation _navigation;
    private readonly IPermissionService _permissionService;
    private ObservableCollection<ReceiveHistoryEntity> _receiveHistory;
    public ObservableCollection<ReceiveHistoryEntity> ReceiveHistory
    {
        get { return _receiveHistory; }
        set { SetProperty(ref _receiveHistory, value); }
    }

    private bool _isLoading;
    public bool IsLoading 
    { 
        get 
        { 
            return _isLoading; 
        }
        set
        {
            SetProperty(ref _isLoading, value);
        }
    }

    public ICommand BackCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand ShowFileInfoCommand { get; }
    public ICommand DeleteSingleHistoryCommand { get; }
    public ICommand ShowOperationContextMenuCommand { get; }

    public HistoryViewModel(IFileShareServiceManager serviceManager, IAlertService alertService, INavigation navigation,IPermissionService permissionService)
    {
        _serviceManager = serviceManager;
        _alertService = alertService;
        _navigation = navigation;
        _permissionService = permissionService;
        _receiveHistory = new ObservableCollection<ReceiveHistoryEntity>();

        BackCommand = new RelayCommand(Back);
        DeleteHistoryCommand = new RelayCommand(async () => await DeleteHistoryAsync());
        OpenFileCommand = new RelayCommand<ReceiveHistoryEntity>(OpenFile);
        ShowFileInfoCommand = new RelayCommand<ReceiveHistoryEntity>(ShowFileInfo);
        DeleteSingleHistoryCommand = new RelayCommand<ReceiveHistoryEntity>(async (history) => await DeleteSingleHistoryAsync(history));
        ShowOperationContextMenuCommand = new RelayCommand<ReceiveHistoryEntity>(async (history) => await ShowOperationContextMenu(history));
        //_ = LoadHistoryAsync();
    }

    public async Task LoadHistoryAsync()
    {
        IsLoading = true;
        try
        {
            var history = await _serviceManager.GetAllReceiveHistoryAsync();
            ReceiveHistory.Clear();
            foreach (var item in history)
            {
                ReceiveHistory.Add(item);
            }
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync($"加载历史记录失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void Back()
    {
        await _navigation.PopAsync();
    }

    private async Task DeleteHistoryAsync()
    {
        var confirmed = await _alertService.DisplayAlertAsync("确认", "确定要清空所有历史记录吗？", "确定", "取消");
        if (confirmed)
        {
            try
            {
                await _serviceManager.ClearReceiveHistoryAsync();
                ReceiveHistory.Clear();
                await _alertService.DisplayToastAsync("历史记录已清空");
            }
            catch (Exception ex)
            {
                await _alertService.DisplayToastAsync($"清空历史记录失败: {ex.Message}");
            }
        }
    }

    private async void OpenFile(ReceiveHistoryEntity? history)
    {
        if (history == null)
        {
            return;
        }
        string fullname = Path.Combine(history.SavePath, history.FileName);
        if (!File.Exists(fullname))
        {
            await _alertService.DisplayToastAsync("文件不存在或路径无效");
            return;
        }

        try
        {
#if ANDROID23_0_OR_GREATER
            // 检查是否是安装包文件
            if (fullname.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                // 检查是否有安装权限
                var status = await _permissionService.CheckInstallPackagePermissionAsync();
                if (!status)
                {
                    // 请求安装权限（实际上对于特殊权限，这通常不会弹出对话框，而是直接返回 Denied）
                    status = await _permissionService.RequestInstallPackagePermissionAsync();
                    if (!status)
                    {
                        // 引导用户到系统设置页面手动开启
                        await _alertService.DisplayToastAsync("请在设置中允许安装未知应用");

                        // 打开应用详情设置页面
                        AppInfo.Current.ShowSettingsUI();
                        return;
                    }
                }
            }
#endif

            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(fullname)
            });
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync($"打开文件失败: {ex.Message}");
        }
    }


    private async Task DeleteSingleHistoryAsync(ReceiveHistoryEntity? history)
    {
        if (history == null)
            return;
        try
        {
            await _serviceManager.DeleteSingleReceiveHistoryAsync(history.Id);
            ReceiveHistory.Remove(history);
            await _alertService.DisplayToastAsync("历史记录已删除");
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync($"删除历史记录失败: {ex.Message}");
        }
    }

    private async Task ShowOperationContextMenu(ReceiveHistoryEntity? history)
    {
        if (history == null)
            return;
        string[] options = { "打开文件", "显示文件信息", "删除记录" };
        string str = await _alertService.DisplayActionSheetAsync("选择操作", "取消", null, options);
        switch (str)
        {
            case "打开文件":
                OpenFile(history);
                break;
            case "显示文件信息":
                ShowFileInfo(history);
                break;
            case "删除记录":
                await DeleteSingleHistoryAsync(history);
                break;
        }
    }

    private async void ShowFileInfo(ReceiveHistoryEntity? history)
    {
        if (history == null)
            return;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"文件名:\t{history.FileName}");
        sb.AppendLine($"路径:\t{history.SavePath}");
        sb.AppendLine($"大小:\t{FormatFileSize(history.FileSize)}");
        sb.AppendLine($"发送者:\t{history.SenderDeviceName}");
        sb.AppendLine($"时间:\t{history.CreatedAt.ToString("yyyy/y/d H:mm")}");

        await _alertService.DisplayAlertAsync(
            "文件信息",
            sb.ToString(),          
            "关闭");
    }

    private string FormatFileSize(long size)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int index = 0;
        double sizeDouble = size;

        while (sizeDouble >= 1024 && index < suffixes.Length - 1)
        {
            sizeDouble /= 1024;
            index++;
        }

        return $"{sizeDouble:F1}{suffixes[index]}";
    }
}
