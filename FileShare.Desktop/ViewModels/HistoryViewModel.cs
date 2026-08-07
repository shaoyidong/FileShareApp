using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileShare.Desktop.ViewModels
{
    public partial class HistoryViewModel : ViewModelBase
    {
        private readonly IFileShareServiceManager _serviceManager;
        private readonly IDialogService _dialogService;
        private readonly Action _backAction;

        [ObservableProperty]
        private ObservableCollection<ReceiveHistoryEntity> _receiveHistory;

        [ObservableProperty]
        private bool _isLoading;

        public ICommand BackCommand { get; }
        public ICommand DeleteHistoryCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand ShowInFileManagerCommand { get; }
        public ICommand DeleteSingleHistoryCommand { get; }

        public HistoryViewModel(IFileShareServiceManager serviceManager, IDialogService dialogService, Action backAction)
        {
            _serviceManager = serviceManager;
            _dialogService = dialogService;
            _backAction = backAction;
            _receiveHistory = new ObservableCollection<ReceiveHistoryEntity>();

            BackCommand = new RelayCommand(Back);
            DeleteHistoryCommand = new RelayCommand(async () => await DeleteHistoryAsync());
            OpenFileCommand = new RelayCommand<ReceiveHistoryEntity>(OpenFile);
            ShowInFileManagerCommand = new RelayCommand<ReceiveHistoryEntity>(ShowInFileManager);
            DeleteSingleHistoryCommand = new RelayCommand<ReceiveHistoryEntity>(async (history) => await DeleteSingleHistoryAsync(history));

            _ = LoadHistoryAsync();
        }

        private async Task LoadHistoryAsync()
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
                await _dialogService.ShowInfomationDialogAsync("错误", $"加载历史记录失败: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void Back()
        {
            _backAction?.Invoke();
        }

        private async Task DeleteHistoryAsync()
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync("确认", "确定要清空所有历史记录吗？");
            if (confirmed)
            {
                try
                {
                    await _serviceManager.ClearReceiveHistoryAsync();
                    ReceiveHistory.Clear();
                }
                catch (Exception ex)
                {
                    await _dialogService.ShowInfomationDialogAsync("错误", $"清空历史记录失败: {ex.Message}");
                }
            }
        }

        private void OpenFile(ReceiveHistoryEntity? history)
        {
            if (history == null)
            {
                return;
            }
            string fullname = Path.Combine(history.SavePath, history.FileName);
            if (!File.Exists(fullname))
            {
                _dialogService.ShowInfomationDialogAsync("错误", "文件不存在或路径无效");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullname,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _dialogService.ShowInfomationDialogAsync("错误", $"打开文件失败: {ex.Message}");
            }
        }

        private void ShowInFileManager(ReceiveHistoryEntity? history)
        {           
            if (history == null) return;
            string fullname = Path.Combine(history.SavePath, history.FileName);
            if (!File.Exists(fullname))
            {
                _dialogService.ShowInfomationDialogAsync("错误", "文件不存在或路径无效");
                return;
            }

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start("explorer.exe", $"/select,\"{fullname}\"");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", $"-R \"{fullname}\"");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // 尝试多种文件管理器（按需调整）
                    string[] fileManagers = { "nautilus", "dolphin", "thunar", "pcmanfm" };
                    bool launched = false;
                    foreach (var fm in fileManagers)
                    {
                        try
                        {
                            Process.Start(fm, $"--select \"{fullname}\"");
                            launched = true;
                            break;
                        }
                        catch { /* 尝试下一个 */ }
                    }
                    if (!launched)
                    {
                        // 降级：只打开目录
                        Process.Start("xdg-open", Path.GetDirectoryName(fullname)!);
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowInfomationDialogAsync("错误", $"打开文件管理器失败: {ex.Message}");
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
            }
            catch (Exception ex)
            {
                await _dialogService.ShowInfomationDialogAsync("错误", $"删除历史记录失败: {ex.Message}");
            }
        }
    }
}