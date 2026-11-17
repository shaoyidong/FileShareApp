using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using Shyd.Prism.Localization;
using Shyd.Prism.Dialogs;
using System;
using System.IO;

using Shyd.Prism.Mvvm;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 接收文件确认弹窗的 ViewModel：展示发送者 / 文件 / 大小 / 保存目录，
/// 接收（Ok）或拒绝（No）关闭，结果携带 <see cref="ReceiveRequestResult"/>。
/// </summary>
public sealed partial class ReceiveRequestViewModel : ViewModelBase, IDialogAware
{
    /// <summary>弹窗的入参与出参键。</summary>
    public static class DialogKeys
    {
        /// <summary>入参：传输信息。</summary>
        public const string Transfer = "transfer";

        /// <summary>入参：保存目录（空表示按类型自动归类）。</summary>
        public const string SaveDirectory = "saveDirectory";

        /// <summary>入参：发送者名称。</summary>
        public const string SenderName = "senderName";

        /// <summary>出参：用户选择结果（<see cref="ReceiveRequestResult"/>）。</summary>
        public const string Result = "result";
    }

    /// <inheritdoc />
    public string Title => Loc.Get("文件传输请求");

    /// <inheritdoc />
    public event Action<IDialogResult>? RequestClose;

    [ObservableProperty] public partial string SenderText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FileText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SizeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DirectoryText { get; set; } = string.Empty;

    /// <summary>是否把对方加入收藏（以后自动接收）。</summary>
    [ObservableProperty] public partial bool AlwaysAccept { get; set; }

    public RelayCommand AcceptCommand { get; }
    public RelayCommand DeclineCommand { get; }

    public ReceiveRequestViewModel()
    {
        AcceptCommand = new RelayCommand(() => Close(accept: true));
        DeclineCommand = new RelayCommand(() => Close(accept: false));
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
        var transfer = parameters.GetValue<FileTransferInfo>(DialogKeys.Transfer);
        parameters.TryGetValue<string>(DialogKeys.SaveDirectory, out var saveDirectory);
        parameters.TryGetValue<string>(DialogKeys.SenderName, out var senderName);

        var displayName = string.IsNullOrEmpty(transfer.RelativePath)
            ? transfer.FileName
            : Path.Combine(transfer.RelativePath, transfer.FileName);

        SenderText = string.IsNullOrWhiteSpace(senderName)
            ? Loc.Get("来自未知设备")
            : Loc.F("来自：{0}", senderName);
        FileText = displayName;
        SizeText = Loc.F("大小：{0}", FormatSize(transfer.FileSize));
        DirectoryText = string.IsNullOrEmpty(saveDirectory)
            ? Loc.Get("（按文件类型自动归类：图片 / 视频 / 音乐 / 文档 / 下载）")
            : saveDirectory;
    }

    private void Close(bool accept) =>
        RequestClose?.Invoke(new DialogResult(
            accept ? ButtonResult.Ok : ButtonResult.No,
            new DialogParameters
            {
                [DialogKeys.Result] = new ReceiveRequestResult(accept, AlwaysAccept)
            }));

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;

        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:F1} {units[index]}";
    }
}
