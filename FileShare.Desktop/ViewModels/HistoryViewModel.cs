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
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.ViewModels
{
    public partial class HistoryViewModel : ViewModelBase
    {
        private readonly IFileShareServiceManager _serviceManager;
        private readonly IDialogService _dialogService;
        private readonly Action _backAction;
        private readonly List<ReceiveHistoryEntity> _allHistory = new();

        [ObservableProperty]
        private ObservableCollection<ReceiveHistoryEntity> _receiveHistory;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _filterText = Loc.Get("全部");

        public ICommand BackCommand { get; }
        public ICommand DeleteHistoryCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand ShowInFileManagerCommand { get; }
        public ICommand DeleteSingleHistoryCommand { get; }
        public ICommand ShowAllCommand { get; }
        public ICommand ShowReceivedCommand { get; }
        public ICommand ShowSentCommand { get; }

        /// <summary>当前筛选：null = 全部，Receive = 接收，Send = 发送。</summary>
        public FileShare.Core.Models.TransferDirection? DirectionFilter { get; private set; }

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
            ShowAllCommand = new RelayCommand(() => ApplyFilter(null, Loc.Get("全部")));
            ShowReceivedCommand = new RelayCommand(() => ApplyFilter(FileShare.Core.Models.TransferDirection.Receive, Loc.Get("仅接收")));
            ShowSentCommand = new RelayCommand(() => ApplyFilter(FileShare.Core.Models.TransferDirection.Send, Loc.Get("仅发送")));

            _ = LoadHistoryAsync();
        }

        /// <summary>
        /// 切换筛选条件并重建显示列表。
        /// </summary>
        private void ApplyFilter(FileShare.Core.Models.TransferDirection? direction, string filterText)
        {
            DirectionFilter = direction;
            FilterText = filterText;
            RefreshView();
        }

        private void RefreshView()
        {
            ReceiveHistory.Clear();

            foreach (var item in _allHistory)
            {
                if (DirectionFilter is null || item.Direction == DirectionFilter)
                {
                    ReceiveHistory.Add(item);
                }
            }

            OnPropertyChanged(nameof(HistoryCountText));
        }

        /// <summary>当前显示的记录条数。</summary>
        public string HistoryCountText => Loc.F("共 {0} 条", ReceiveHistory.Count);

        private async Task LoadHistoryAsync()
        {
            IsLoading = true;
            try
            {
                var history = await _serviceManager.GetAllReceiveHistoryAsync();
                _allHistory.Clear();
                _allHistory.AddRange(history);
                RefreshView();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("加载历史记录失败: {0}", ex.Message));
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
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(Loc.Get("确认"), Loc.Get("确定要清空所有历史记录吗？"));
            if (confirmed)
            {
                try
                {
                    await _serviceManager.ClearReceiveHistoryAsync();
                    ReceiveHistory.Clear();
                }
                catch (Exception ex)
                {
                    await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("清空历史记录失败: {0}", ex.Message));
                }
            }
        }

        /// <summary>
        /// 解析历史记录对应的本机文件路径。
        /// <para>优先用绝对路径 <see cref="ReceiveHistoryEntity.FilePath"/>：
        /// 接收记录指向落盘位置，发送记录指向源文件位置（旧版本发送记录没有该字段，退回 SavePath + FileName）。</para>
        /// </summary>
        private static string? ResolveFilePath(ReceiveHistoryEntity history)
        {
            if (!string.IsNullOrWhiteSpace(history.FilePath))
            {
                return File.Exists(history.FilePath) ? history.FilePath : null;
            }

            if (string.IsNullOrWhiteSpace(history.SavePath))
            {
                return null;
            }

            var legacy = Path.Combine(history.SavePath, history.FileName);
            return File.Exists(legacy) ? legacy : null;
        }

        private void OpenFile(ReceiveHistoryEntity? history)
        {
            if (history == null)
            {
                return;
            }

            var fullname = ResolveFilePath(history);
            if (fullname is null)
            {
                _ = _dialogService.ShowInfomationDialogAsync(
                    Loc.Get("无法打开"),
                    history.IsSuccess
                        ? Loc.Get("找不到该文件（可能已被移动或删除）。")
                        : Loc.F("这条记录的状态是「{0}」，没有生成文件。{1}{2}", history.StatusText, Environment.NewLine, history.ErrorMessage));
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
                _ = _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("打开文件失败: {0}", ex.Message));
            }
        }

        private void ShowInFileManager(ReceiveHistoryEntity? history)
        {           
            if (history == null) return;

            var fullname = ResolveFilePath(history);
            if (fullname is null)
            {
                _ = _dialogService.ShowInfomationDialogAsync(
                    Loc.Get("无法定位"),
                    history.IsSuccess
                        ? Loc.Get("找不到该文件（可能已被移动或删除）。")
                        : Loc.F("这条记录的状态是「{0}」，没有生成文件。", history.StatusText));
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
                _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("打开文件管理器失败: {0}", ex.Message));
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
                await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("删除历史记录失败: {0}", ex.Message));
            }
        }
    }
}
