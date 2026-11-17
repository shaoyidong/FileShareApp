using CommunityToolkit.Mvvm.Input;
using Shyd.Prism.Localization;
using Shyd.Prism.Dialogs;
using System;

using Shyd.Prism.Mvvm;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 「发送文本」弹窗的 ViewModel：编辑文本，发送（Ok）或取消（Cancel）关闭。
/// </summary>
public sealed partial class SendTextViewModel : ViewModelBase, IDialogAware
{
    /// <summary>弹窗的入参与出参键。</summary>
    public static class DialogKeys
    {
        /// <summary>弹窗注册名（Program 里以此名称注册，调用方可按名称打开）。</summary>
        public const string DialogName = "SendTextDialog";

        /// <summary>入参：接收方设备名。</summary>
        public const string TargetDeviceName = "targetDeviceName";

        /// <summary>出参：用户输入的文本（取消时不存在）。</summary>
        public const string Text = "text";
    }

    /// <inheritdoc />
    public string Title => Loc.Get("发送文本");

    /// <inheritdoc />
    public event Action<IDialogResult>? RequestClose;

    /// <summary>用户输入的文本。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>状态栏展示的接收方描述。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string TargetText { get; set; } = string.Empty;

    public RelayCommand SendCommand { get; }
    public RelayCommand CancelCommand { get; }

    public SendTextViewModel()
    {
        SendCommand = new RelayCommand(Send);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(
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
        parameters.TryGetValue<string>(DialogKeys.TargetDeviceName, out var targetDeviceName);
        TargetText = string.IsNullOrWhiteSpace(targetDeviceName)
            ? Loc.Get("接收方：未选择设备")
            : Loc.F("接收方：{0}", targetDeviceName);
    }

    private void Send()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        RequestClose?.Invoke(new DialogResult(ButtonResult.Ok, new DialogParameters
        {
            [DialogKeys.Text] = Text
        }));
    }
}
