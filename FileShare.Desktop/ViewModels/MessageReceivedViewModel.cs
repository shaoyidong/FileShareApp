using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shyd.Prism.Localization;
using Shyd.Prism.Dialogs;
using System;

using Shyd.Prism.Mvvm;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 收到文本消息展示弹窗的 ViewModel（非模态、不抢焦点）：展示发送者与全文，
/// 关闭按钮请求关闭。复制全文需要访问窗口剪贴板，保留在 code-behind。
/// 宿主窗口以非激活方式显示由 <see cref="IDialogAwareWindowOptions"/> 配置。
/// </summary>
public sealed partial class MessageReceivedViewModel : ViewModelBase, IDialogAware, IDialogAwareWindowOptions
{
    /// <summary>弹窗的入参键。</summary>
    public static class DialogKeys
    {
        /// <summary>入参：发送者名称。</summary>
        public const string SenderName = "senderName";

        /// <summary>入参：消息全文。</summary>
        public const string Message = "message";
    }

    /// <inheritdoc />
    public string Title => Loc.Get("收到文本消息");

    /// <inheritdoc />
    public event Action<IDialogResult>? RequestClose;

    [ObservableProperty] public partial string SenderText { get; set; } = string.Empty;

    /// <summary>收到的消息全文。</summary>
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;

    public RelayCommand CloseCommand { get; }

    public MessageReceivedViewModel()
    {
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(
            new DialogResult(ButtonResult.Cancel)));
    }

    /// <inheritdoc />
    public bool CanCloseDialog() => true;

    /// <inheritdoc />
    public void OnDialogClosed()
    {
    }

    /// <inheritdoc />
    public void OnDialogOpened(IDialogParameters parameters)
    {
        parameters.TryGetValue<string>(DialogKeys.SenderName, out var senderName);
        parameters.TryGetValue<string>(DialogKeys.Message, out var message);

        Message = message ?? string.Empty;
        SenderText = string.IsNullOrWhiteSpace(senderName)
            ? Loc.Get("收到文本消息")
            : Loc.F("收到来自 {0} 的文本消息", senderName);
    }

    /// <inheritdoc />
    public void ConfigureWindowOptions(DialogWindowOptions options)
    {
        // 消息窗不抢焦点：宿主窗口以非激活方式显示（沿用原 MessageReceivedWindow.ShowActivated=False）
        options.ShowActivated = false;
    }
}
