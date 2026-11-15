using Avalonia.Controls;
using Avalonia.Interactivity;
using FileShare.Desktop.ViewModels;

namespace FileShare.Desktop.Views
{
    public partial class HistoryView : UserControl
    {
        /// <summary>距底部小于该像素值时触发下一页加载。</summary>
        private const double LoadMoreThresholdPixels = 200;

        public HistoryView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 滚动接近底部时通知 ViewModel 续载下一页（键集分页）。
        /// </summary>
        private void OnHistoryScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer scroll || DataContext is not HistoryViewModel viewModel)
            {
                return;
            }

            var remaining = scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;
            if (remaining <= LoadMoreThresholdPixels)
            {
                viewModel.LoadMoreCommand.Execute(null);
            }
        }
    }
}
