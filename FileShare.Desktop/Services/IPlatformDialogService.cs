using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services
{
    /// <summary>
    /// 平台对话框服务接口：操作系统提供的文件/文件夹选择器与消息框
    /// （区别于 Services.Dialogs.IDialogService 的自研注册弹窗），便于单元测试
    /// </summary>
    public interface IPlatformDialogService
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

        /// <summary>
        /// 显示确认对话框
        /// </summary>
        /// <param name="title">对话框标题</param>
        /// <param name="message">对话框消息</param>
        /// <param name="owner">对话框所有者</param>
        /// <returns>用户是否点击了确认按钮</returns>
        Task<bool> ShowInfomationDialogAsync(string title, string message, Window? owner = null);
    }
}