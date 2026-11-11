using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Localization;
using FileShare.Desktop.Services;
using FileShare.Desktop.Views;
using FileShare.LocalSend;
using System;
using System.Threading.Tasks;

namespace FileShare.Desktop.Helpers;

/// <summary>
/// <see cref="IAppUiBridge"/> 的桌面端实现：把协议适配层 / 网页分享的通知、文案、
/// 接收确认与传输状态转给桌面 UI。
/// </summary>
public sealed class LocalSendUiBridge : ILocalSendUiBridge
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);

    private readonly IToastService _toastService;
    private readonly IAppSettingsService _settingsService;

    public LocalSendUiBridge(IToastService toastService, IAppSettingsService settingsService)
    {
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <inheritdoc />
    public void ShowToast(string title, string message, Action? onClick = null) =>
        _toastService.Show(title, message, onClick);

    /// <inheritdoc />
    public void ShowTextMessage(string senderName, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var window = new MessageReceivedWindow(senderName, message);
            // 主窗口隐藏到托盘时不能作为 owner（Avalonia 要求 owner 已显示），改为独立窗口
            if (owner is { IsVisible: true })
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }

            // 主窗口在托盘 / 后台时补一条通知（前台时 ToastService 会自动抑制），
            // 点击通知把消息窗口唤到前面
            if (owner is not null)
            {
                _toastService.Show(
                    Loc.Get("收到文本消息"),
                    message.Length > 60 ? message[..60] + "…" : message,
                    () => Dispatcher.UIThread.Post(() =>
                    {
                        ActivateWindow(owner);
                        window.Activate();
                    }));
            }
        });
    }

    /// <inheritdoc />
    public string GetText(string key) => Loc.Get(key);

    /// <inheritdoc />
    public void ReportTransfer(ExternalTransferUpdate update) =>
        App.MainViewModel?.ReportExternalTransfer(update);

    /// <inheritdoc />
    public async Task<ExternalReceiveDecision> ConfirmReceiveAsync(ExternalReceiveRequest request)
    {
        var settings = _settingsService.Current;

        // 与 FileShare 自有协议一致：收藏设备 + 开启自动接收 → 免确认
        if (settings.AutoAcceptFromFavorites && settings.IsFavorite(request.SenderId))
        {
            return ExternalReceiveDecision.AcceptOnce;
        }

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            // 没有主窗口（无界面场景）：保守拒绝，不静默接收
            return ExternalReceiveDecision.Declined;
        }

        var transfer = new FileTransferInfo
        {
            TransferId = Guid.NewGuid().ToString("N"),
            FileName = request.FileName,
            FileSize = request.FileSize,
            SenderId = request.SenderId,
            ReceiverId = "local-device",
            Direction = TransferDirection.Receive,
            Status = TransferStatus.Pending
        };

        // 窗口活跃：直接弹确认窗，不再无条件 Show()+Activate() 抢焦点
        var isActive = await Dispatcher.UIThread.InvokeAsync(() => owner.IsActive);
        if (isActive)
        {
            return await ShowConfirmWindowAsync(owner, request, transfer).ConfigureAwait(false);
        }

        // 托盘 / 后台：只弹通知；点击通知才显示主窗口并弹确认窗（60 秒超时保留）
        var decisionTcs = new TaskCompletionSource<ExternalReceiveDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _toastService.Show(
            Loc.Get("收到文件传输请求"),
            Loc.F("{0} 想发送：{1}", request.SenderName, request.FileName),
            () => Dispatcher.UIThread.Post(async () =>
            {
                ActivateWindow(owner);
                var decision = await ShowConfirmWindowAsync(owner, request, transfer).ConfigureAwait(true);
                decisionTcs.TrySetResult(decision);
            }));

        var completed = await Task.WhenAny(decisionTcs.Task, Task.Delay(ConfirmTimeout)).ConfigureAwait(false);
        return completed == decisionTcs.Task
            ? await decisionTcs.Task.ConfigureAwait(false)
            : ExternalReceiveDecision.Declined;
    }

    /// <summary>在 UI 线程弹接收确认窗，并等待用户选择（最长 60 秒）。</summary>
    private async Task<ExternalReceiveDecision> ShowConfirmWindowAsync(
        Window owner, ExternalReceiveRequest request, FileTransferInfo transfer)
    {
        var tcs = new TaskCompletionSource<ReceiveRequestResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReceiveRequestWindow? window = null;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                window = new ReceiveRequestWindow(transfer, request.SaveDirectory, request.SenderName);
                tcs.TrySetResult(await window.ShowDialog<ReceiveRequestResult?>(owner));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(ConfirmTimeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            Dispatcher.UIThread.Post(() => window?.Close(null));
            return ExternalReceiveDecision.Declined;
        }

        try
        {
            var result = await tcs.Task.ConfigureAwait(false);
            if (result is null)
            {
                return ExternalReceiveDecision.Declined;
            }

            if (result.AlwaysAccept)
            {
                _settingsService.Update(s =>
                {
                    if (!s.IsFavorite(request.SenderId))
                    {
                        s.ToggleFavorite(request.SenderId);
                    }
                });
            }

            return new ExternalReceiveDecision(result.Accept, result.AlwaysAccept);
        }
        catch (Exception)
        {
            return ExternalReceiveDecision.Declined;
        }
    }

    private static void ActivateWindow(Window owner)
    {
        owner.Show();
        if (owner.WindowState == WindowState.Minimized)
        {
            owner.WindowState = WindowState.Normal;
        }

        owner.Activate();
    }
}
