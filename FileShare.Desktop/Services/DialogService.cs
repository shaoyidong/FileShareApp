using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services
{
    /// <summary>
    /// 对话框服务实现，用于生产环境
    /// </summary>
    public class DialogService : IDialogService
    {
        private readonly IClassicDesktopStyleApplicationLifetime _appLifetime;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="appLifetime">应用程序生命周期</param>
        public DialogService(IClassicDesktopStyleApplicationLifetime appLifetime)
        {
            _appLifetime = appLifetime;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options)
        {
            var topLevel = TopLevel.GetTopLevel(_appLifetime.MainWindow);
            return await topLevel.StorageProvider.OpenFilePickerAsync(options);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options)
        {
            var topLevel = TopLevel.GetTopLevel(_appLifetime.MainWindow);
            return await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        }

        /// <inheritdoc/>
        public async Task<bool> ShowConfirmationDialogAsync(string title, string message)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(title, message, MsBox.Avalonia.Enums.ButtonEnum.YesNo);
            var result = await box.ShowWindowDialogAsync(_appLifetime.MainWindow);
            return result == MsBox.Avalonia.Enums.ButtonResult.Yes;
        }
    }
}