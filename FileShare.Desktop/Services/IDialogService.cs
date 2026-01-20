using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services
{
    /// <summary>
    /// 对话框服务接口，用于抽象各种对话框功能，便于单元测试
    /// </summary>
    public interface IDialogService
    {
        /// <summary>
        /// 打开文件选择对话框
        /// </summary>
        /// <param name="options">文件选择选项</param>
        /// <returns>选择的文件列表</returns>
        Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options,Visual? visual = null);

        /// <summary>
        /// 打开文件夹选择对话框
        /// </summary>
        /// <param name="options">文件夹选择选项</param>
        /// <returns>选择的文件夹列表</returns>
        Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options,Visual? visual=null);

        /// <summary>
        /// 显示确认对话框
        /// </summary>
        /// <param name="title">对话框标题</param>
        /// <param name="message">对话框消息</param>
        /// <param name="owner">对话框所有者</param>
        /// <returns>用户是否点击了确认按钮</returns>
        Task<bool> ShowConfirmationDialogAsync(string title, string message,Window? owner=null);
    }
}