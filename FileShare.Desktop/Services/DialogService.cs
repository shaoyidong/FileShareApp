using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Avalonia;

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
        public async Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options,Visual? visual = null)
        {
            var actualVisual = visual ?? _appLifetime.MainWindow;
            var topLevel = TopLevel.GetTopLevel(actualVisual);
            if (topLevel?.StorageProvider == null)
                return new List<IStorageFile>();
            return await topLevel.StorageProvider.OpenFilePickerAsync(options);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options,Visual? visual = null)
        {
            var actualVisual = visual ?? _appLifetime.MainWindow;
            var topLevel = TopLevel.GetTopLevel(actualVisual);
            if (topLevel?.StorageProvider == null)
                return new List<IStorageFolder>();
            return await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        }

        /// <inheritdoc/>
        public async Task<bool> ShowConfirmationDialogAsync(string title, string message, Window? owner = null)
        {
            var actualOwner = owner ?? _appLifetime.MainWindow;            
            var box = MessageBoxManager.GetMessageBoxStandard(title, message, MsBox.Avalonia.Enums.ButtonEnum.YesNo);           
            var result = actualOwner == null ? await box.ShowWindowAsync() : await box.ShowWindowDialogAsync(actualOwner);
            return result == MsBox.Avalonia.Enums.ButtonResult.Yes;
        }
    }
}