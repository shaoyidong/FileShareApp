using CommunityToolkit.Mvvm.Messaging.Messages;
using System.Threading.Tasks;

namespace FileShare.Desktop.ViewModels.Messages;

/// <summary>
/// 确认对话框消息
/// </summary>
public class ConfirmationMessage : ValueChangedMessage<bool>
{
    /// <summary>
    /// 对话框标题
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// 对话框消息
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// 用于等待对话框结果的TaskCompletionSource
    /// </summary>
    public TaskCompletionSource<bool> CompletionSource { get; }

    /// <summary>
    /// 初始化确认对话框消息
    /// </summary>
    /// <param name="message">对话框消息</param>
    /// <param name="title">对话框标题</param>
    /// <param name="value">默认值</param>
    public ConfirmationMessage(string message, string title, bool value) : base(value)
    {
        Message = message;
        Title = title;
        CompletionSource = new TaskCompletionSource<bool>();
    }
}