using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models.Entities;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using Shyd.Prism.Regions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Shyd.Prism.Localization;

using Shyd.Prism.Mvvm;
using FileShare.Desktop.Helpers;

namespace FileShare.Desktop.ViewModels
{
    public partial class HistoryViewModel : ViewModelBase
    {
        /// <summary>每页加载条数：首屏只取一页，滚动到底部再续载，避免万条级记录一次拉全表。</summary>
        private const int PageSize = 100;

        private readonly IFileShareServiceManager _serviceManager;
        private readonly IPlatformDialogService _dialogService;
        private readonly IRegionManager _regionManager;
        private readonly IClipboardService _clipboardService;
        private readonly IShellService _shellService;

        /// <summary>当前筛选下已加载的记录（新的在前）。行的双列布局由 <see cref="RebuildRows"/> 拼装。</summary>
        private readonly List<HistoryItemViewModel> _loadedItems = new();

        /// <summary>键集分页游标：已加载记录中最小的 Id，下一页取 Id 小于它的记录。</summary>
        private int? _lastLoadedId;

        /// <summary>当前筛选下的总条数（COUNT 查询，供「共 N 条」与是否还有下一页）。</summary>
        private int _totalCount;

        /// <summary>串行化首屏 / 续载：滚动事件会高频触发 LoadMoreCommand。</summary>
        private bool _isBusy;

        /// <summary>显示用行集合：每行左右两个卡位，保留原双列卡片外观。</summary>
        [ObservableProperty]
        public partial ObservableCollection<HistoryRowViewModel> ReceiveHistoryRows { get; set; }

        [ObservableProperty]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        public partial string FilterText { get; set; } = Loc.Get("全部");

        /// <summary>是否还有未加载的更早记录。</summary>
        [ObservableProperty]
        public partial bool HasMore { get; set; }
        public ICommand BackCommand { get; }
        public ICommand DeleteHistoryCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand ShowInFileManagerCommand { get; }
        public ICommand DeleteSingleHistoryCommand { get; }
        public ICommand CopyMessageCommand { get; }
        public ICommand ShowAllCommand { get; }
        public ICommand ShowReceivedCommand { get; }
        public ICommand ShowSentCommand { get; }
        public ICommand LoadMoreCommand { get; }

        /// <summary>当前筛选：null = 全部，Receive = 接收，Send = 发送。</summary>
        public FileShare.Core.Models.TransferDirection? DirectionFilter { get; private set; }

        /// <summary>条数文案：未加载完时附上已加载数，提示还能继续往下滚。</summary>
        public string HistoryCountText => _totalCount > 0 && _loadedItems.Count < _totalCount
            ? Loc.F("共 {0} 条（已加载 {1}）", _totalCount, _loadedItems.Count)
            : Loc.F("共 {0} 条", _loadedItems.Count);

        public HistoryViewModel(
            IFileShareServiceManager serviceManager,
            IPlatformDialogService dialogService,
            IRegionManager regionManager,
            IClipboardService clipboardService,
            IShellService shellService)
        {
            _serviceManager = serviceManager;
            _dialogService = dialogService;
            _regionManager = regionManager ?? throw new ArgumentNullException(nameof(regionManager));
            _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
            _shellService = shellService ?? throw new ArgumentNullException(nameof(shellService));
            ReceiveHistoryRows = new ObservableCollection<HistoryRowViewModel>();

            BackCommand = new RelayCommand(Back);
            DeleteHistoryCommand = new RelayCommand(async () => await DeleteHistoryAsync());
            OpenFileCommand = new RelayCommand<HistoryItemViewModel>(OpenFile);
            ShowInFileManagerCommand = new RelayCommand<HistoryItemViewModel>(ShowInFileManager);
            DeleteSingleHistoryCommand = new RelayCommand<HistoryItemViewModel>(async (history) => await DeleteSingleHistoryAsync(history));
            CopyMessageCommand = new RelayCommand<HistoryItemViewModel>(async (history) => await CopyMessageAsync(history));
            ShowAllCommand = new RelayCommand(() => ApplyFilter(null, Loc.Get("全部")));
            ShowReceivedCommand = new RelayCommand(() => ApplyFilter(FileShare.Core.Models.TransferDirection.Receive, Loc.Get("仅接收")));
            ShowSentCommand = new RelayCommand(() => ApplyFilter(FileShare.Core.Models.TransferDirection.Send, Loc.Get("仅发送")));
            LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);

            _ = LoadHistoryAsync();
        }

        /// <summary>
        /// 切换筛选条件并按新条件重新查询（重置游标与总数）。
        /// </summary>
        private void ApplyFilter(FileShare.Core.Models.TransferDirection? direction, string filterText)
        {
            DirectionFilter = direction;
            FilterText = filterText;
            _ = LoadHistoryAsync();
        }

        private void Back()
        {
            _regionManager.GoBack(NavigationPages.MainRegion);
        }

        /// <summary>首屏：取第一页 + 总条数，重置游标。</summary>
        private async Task LoadHistoryAsync()
        {
            if (_isBusy)
            {
                return;
            }

            _isBusy = true;
            IsLoading = true;
            try
            {
                var page = await _serviceManager.GetReceiveHistoryPageAsync(PageSize, null, DirectionFilter);
                _totalCount = await _serviceManager.CountReceiveHistoryAsync(DirectionFilter);

                _loadedItems.Clear();
                _loadedItems.AddRange(page.Select(HistoryItemViewModel.FromEntity));
                RebuildRows();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("加载历史记录失败: {0}", ex.Message));
            }
            finally
            {
                IsLoading = false;
                _isBusy = false;
            }
        }

        /// <summary>续载：从游标继续取下一页并追加到列表（滚动接近底部时由视图触发）。</summary>
        private async Task LoadMoreAsync()
        {
            if (_isBusy || IsLoading || !HasMore)
            {
                return;
            }

            _isBusy = true;
            try
            {
                var page = await _serviceManager.GetReceiveHistoryPageAsync(PageSize, _lastLoadedId, DirectionFilter);
                if (page.Count > 0)
                {
                    _loadedItems.AddRange(page.Select(HistoryItemViewModel.FromEntity));
                    RebuildRows();
                }
            }
            catch (Exception ex)
            {
                await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("加载历史记录失败: {0}", ex.Message));
            }
            finally
            {
                _isBusy = false;
            }
        }

        /// <summary>
        /// 把已加载记录按两个一行拼装进显示集合，并同步游标 / 是否还有更多。
        /// 拼装是纯指针操作（复用现有 VM），万条级也只有几千行，重建开销可忽略。
        /// </summary>
        private void RebuildRows()
        {
            _lastLoadedId = _loadedItems.Count > 0 ? _loadedItems[^1].Id : null;
            HasMore = _loadedItems.Count < _totalCount;

            var rows = new ObservableCollection<HistoryRowViewModel>();
            for (var i = 0; i < _loadedItems.Count; i += 2)
            {
                rows.Add(new HistoryRowViewModel(
                    _loadedItems[i],
                    i + 1 < _loadedItems.Count ? _loadedItems[i + 1] : null));
            }

            ReceiveHistoryRows = rows;
            OnPropertyChanged(nameof(HistoryCountText));
        }

        private async Task DeleteHistoryAsync()
        {
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(Loc.Get("确认"), Loc.Get("确定要清空所有历史记录吗？"));
            if (confirmed)
            {
                try
                {
                    await _serviceManager.ClearReceiveHistoryAsync();
                    _loadedItems.Clear();
                    _totalCount = 0;
                    RebuildRows();
                }
                catch (Exception ex)
                {
                    await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("清空历史记录失败: {0}", ex.Message));
                }
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
                if (!await _clipboardService.SetTextAsync(history.Message))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _ = _dialogService.ShowInfomationDialogAsync(
                    Loc.Get("错误"), Loc.F("复制失败: {0}", ex.Message));
            }
        }

        private void OpenFile(HistoryItemViewModel? history)
        {
            if (history == null)
            {
                return;
            }

            var fullname = history.ResolveLocalFile();
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
                _shellService.OpenFile(fullname);
            }
            catch (Exception ex)
            {
                _ = _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("打开文件失败: {0}", ex.Message));
            }
        }

        private void ShowInFileManager(HistoryItemViewModel? history)
        {
            if (history == null) return;

            var fullname = history.ResolveLocalFile();
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
                _shellService.RevealInFileManager(fullname);
            }
            catch (Exception ex)
            {
                _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("打开文件管理器失败: {0}", ex.Message));
            }
        }

        private async Task DeleteSingleHistoryAsync(HistoryItemViewModel? history)
        {
            if (history == null)
                return;
            try
            {
                await _serviceManager.DeleteSingleReceiveHistoryAsync(history.Id);
                _loadedItems.Remove(history);
                _totalCount--;
                RebuildRows();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowInfomationDialogAsync(Loc.Get("错误"), Loc.F("删除历史记录失败: {0}", ex.Message));
            }
        }
    }
}
