using Avalonia.Controls;
using Avalonia.Interactivity;
using FileShare.Core.Models;
using System.IO;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.Views;

/// <summary>
/// 接收请求的返回值。
/// </summary>
/// <param name="Accept">是否接收。</param>
/// <param name="AlwaysAccept">是否把对方加入收藏（以后自动接收）。</param>
public sealed record ReceiveRequestResult(bool Accept, bool AlwaysAccept);

/// <summary>
/// 接收文件的模态确认窗口（P0：原来只在任务列表里内联两个按钮，用户很容易错过 60 秒确认窗口）。
/// </summary>
public partial class ReceiveRequestWindow : Window
{
    public ReceiveRequestWindow()
        : this(new FileTransferInfo
        {
            TransferId = string.Empty,
            FileName = string.Empty,
            FileSize = 0,
            SenderId = string.Empty,
            ReceiverId = string.Empty
        }, null, null)
    {
        // 供 XAML 预览器使用
    }

    public ReceiveRequestWindow(FileTransferInfo transfer, string? saveDirectory, string? senderName)
    {
        InitializeComponent();

        var displayName = string.IsNullOrEmpty(transfer.RelativePath)
            ? transfer.FileName
            : Path.Combine(transfer.RelativePath, transfer.FileName);

        SenderText.Text = string.IsNullOrWhiteSpace(senderName)
            ? Loc.Get("来自未知设备")
            : Loc.F("来自：{0}", senderName);

        FileText.Text = displayName;
        SizeText.Text = Loc.F("大小：{0}", FormatSize(transfer.FileSize));
        DirectoryText.Text = string.IsNullOrEmpty(saveDirectory)
            ? Loc.Get("（按文件类型自动归类：图片 / 视频 / 音乐 / 文档 / 下载）")
            : saveDirectory;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Close(new ReceiveRequestResult(true, AlwaysAcceptCheck.IsChecked == true));
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        Close(new ReceiveRequestResult(false, false));
    }

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
