using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using FileShare.Mobile.Services;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<HistoryViewModel> _logger;
    private ObservableCollection<HistoryItemViewModel> _receiveHistory;
    public ObservableCollection<HistoryItemViewModel> ReceiveHistory
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
    public ICommand CopyMessageCommand { get; }

    public HistoryViewModel(IFileShareServiceManager serviceManager, IAlertService alertService, INavigation navigation,IPermissionService permissionService, ILoggerFactory loggerFactory)
    {
        _serviceManager = serviceManager;
        _alertService = alertService;
        _navigation = navigation;
        _permissionService = permissionService;
        _logger = loggerFactory.CreateLogger<HistoryViewModel>();
        _receiveHistory = new ObservableCollection<HistoryItemViewModel>();

        BackCommand = new RelayCommand(Back);
        DeleteHistoryCommand = new RelayCommand(async () => await DeleteHistoryAsync());
        OpenFileCommand = new RelayCommand<HistoryItemViewModel>(OpenFile);
        ShowFileInfoCommand = new RelayCommand<HistoryItemViewModel>(ShowFileInfo);
        DeleteSingleHistoryCommand = new RelayCommand<HistoryItemViewModel>(async (history) => await DeleteSingleHistoryAsync(history));
        ShowOperationContextMenuCommand = new RelayCommand<HistoryItemViewModel>(async (history) => await ShowOperationContextMenu(history));
        CopyMessageCommand = new RelayCommand<HistoryItemViewModel>(async (history) => await CopyMessageAsync(history));
        //_ = LoadHistoryAsync();
    }

    public async Task LoadHistoryAsync()
    {
        IsLoading = true;
        try
        {
            var history = await _serviceManager.GetAllReceiveHistoryAsync();
            ReceiveHistory.Clear();
            foreach (var entity in history)
            {
                ReceiveHistory.Add(HistoryItemViewModel.FromEntity(entity));
            }
            _logger.LogInformation("加载历史记录成功，共 {Count} 条", ReceiveHistory.Count);
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync($"加载历史记录失败: {ex.Message}");
            _logger.LogError(ex, "加载历史记录失败");
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
                _logger.LogInformation("历史记录已清空");
            }
            catch (Exception ex)
            {
                await _alertService.DisplayToastAsync($"清空历史记录失败: {ex.Message}");
                _logger.LogError(ex, "清空历史记录失败");
            }
        }
    }

    private async void OpenFile(HistoryItemViewModel? history)
    {
        if (history == null)
        {
            return;
        }
        string? fullname = history.ResolveLocalFile();
        if (fullname is null)
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


    private async Task DeleteSingleHistoryAsync(HistoryItemViewModel? history)
    {
        if (history == null)
            return;
        try
        {
            await _serviceManager.DeleteSingleReceiveHistoryAsync(history.Id);
            ReceiveHistory.Remove(history);
            await _alertService.DisplayToastAsync("历史记录已删除");
            _logger.LogInformation("删除历史记录成功: {FileName}", history.FileName);
        }
        catch (Exception ex)
        {
            await _alertService.DisplayToastAsync($"删除历史记录失败: {ex.Message}");
            _logger.LogError(ex, "删除历史记录失败: {FileName}", history.FileName);
        }
    }

    private async Task ShowOperationContextMenu(HistoryItemViewModel? history)
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

    /// <summary>把文本消息全文复制到系统剪贴板（消息记录没有可打开的文件）。</summary>
    private async Task CopyMessageAsync(HistoryItemViewModel? history)
    {
        if (history is null || string.IsNullOrEmpty(history.Message))
        {
            return;
        }

        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default
                .SetTextAsync(history.Message).ConfigureAwait(false);
            await _alertService.DisplayToastAsync("已复制文本消息").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制文本消息失败");
        }
    }

    private async void ShowFileInfo(HistoryItemViewModel? history)
    {
        if (history == null)
            return;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"文件名:\t{history.FileName}");
        sb.AppendLine($"路径:\t{history.SavePath}");
        sb.AppendLine($"大小:\t{FormatFileSize(history.FileSize)}");
        sb.AppendLine($"发送者:\t{history.SenderDeviceName}");
        sb.AppendLine($"时间:\t{history.CreatedAtLocal:yyyy/MM/dd HH:mm}");
        if (history.IsMessage)
        {
            sb.AppendLine($"全文:\t{history.Message}");
        }

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
