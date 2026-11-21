using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Desktop.Services;
using Shyd.Prism.Localization;
using Shyd.Prism.Dialogs;
using System;
using System.Threading.Tasks;

using Shyd.Prism.Mvvm;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 收到文本消息展示弹窗的 ViewModel（非模态、不抢焦点）：展示发送者与全文，
/// 复制全文与关闭都通过命令完成，剪贴板访问由 <see cref="IClipboardService"/> 注入。
/// 宿主窗口以非激活方式显示由 <see cref="IDialogAwareWindowOptions"/> 配置。
/// </summary>
public sealed partial class MessageReceivedViewModel : ViewModelBase, IDialogAware, IDialogAwareWindowOptions
{
    private readonly IClipboardService _clipboardService;
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

    /// <summary>复制操作的结果提示（成功 / 剪贴板不可用 / 失败原因）。</summary>
    [ObservableProperty] public partial string CopyHintText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsCopyHintVisible { get; set; }

    public RelayCommand CloseCommand { get; }

    public AsyncRelayCommand CopyCommand { get; }

    /// <summary>设计器 / 单元测试使用的无参构造。</summary>
    public MessageReceivedViewModel() : this(NullClipboardService.Instance)
    {
    }

    public MessageReceivedViewModel(IClipboardService clipboardService)
    {
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(
            new DialogResult(ButtonResult.Cancel)));
        CopyCommand = new AsyncRelayCommand(CopyAsync);
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

        CopyHintText = string.Empty;
        IsCopyHintVisible = false;
    }

    /// <summary>复制全文；结果通过 <see cref="CopyHintText"/> 展示。</summary>
    private async Task CopyAsync()
    {
        if (string.IsNullOrEmpty(Message))
        {
            return;
        }

        try
        {
            CopyHintText = await _clipboardService.SetTextAsync(Message)
                ? Loc.Get("已复制文本消息")
                : Loc.Get("剪贴板不可用");
        }
        catch (Exception ex)
        {
            CopyHintText = Loc.F("复制失败: {0}", ex.Message);
        }

        IsCopyHintVisible = true;
    }

    /// <inheritdoc />
    public void ConfigureWindowOptions(DialogWindowOptions options)
    {
        // 消息窗不抢焦点：宿主窗口以非激活方式显示（沿用原 MessageReceivedWindow.ShowActivated=False）
        options.ShowActivated = false;
    }
}
