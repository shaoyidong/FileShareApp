using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Localization;
using FileShare.Desktop.Services;
using FileShare.Desktop.Services.Dialogs;
using FileShare.Desktop.ViewModels;
using FileShare.LocalSend;
using System;
using System.Threading.Tasks;

namespace FileShare.Desktop.Helpers;

/// <summary>
/// <see cref="IAppUiBridge"/> 的桌面端实现：把协议适配层 / 网页分享的通知、文案、
/// 接收确认与传输状态转给桌面 UI。弹窗统一走 <see cref="IDialogService"/>。
/// </summary>
public sealed class LocalSendUiBridge : ILocalSendUiBridge
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);

    private readonly IToastService _toastService;
    private readonly IAppSettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly ITransferEventBus _transferEvents;

    public LocalSendUiBridge(IToastService toastService, IAppSettingsService settingsService, IDialogService dialogs, ITransferEventBus transferEvents)
    {
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _transferEvents = transferEvents ?? throw new ArgumentNullException(nameof(transferEvents));
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
            var window = _dialogs.Show<MessageReceivedViewModel>(new DialogParameters
            {
                [MessageReceivedViewModel.DialogKeys.SenderName] = senderName,
                [MessageReceivedViewModel.DialogKeys.Message] = message
            });

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
                        window?.Activate();
                    }));
            }
        });
    }

    /// <inheritdoc />
    public string GetText(string key) => Loc.Get(key);

    /// <inheritdoc />
    public void ReportTransfer(ExternalTransferUpdate update) =>
        _transferEvents.PublishExternalTransfer(update);

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
            return await ShowConfirmDialogAsync(owner, request, transfer).ConfigureAwait(false);
        }

        // 托盘 / 后台：只弹通知；点击通知才显示主窗口并弹确认窗（60 秒总超时保留）
        var decisionTcs = new TaskCompletionSource<ExternalReceiveDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _toastService.Show(
            Loc.Get("收到文件传输请求"),
            Loc.F("{0} 想发送：{1}", request.SenderName, request.FileName),
            () => Dispatcher.UIThread.Post(async () =>
            {
                ActivateWindow(owner);
                var decision = await ShowConfirmDialogAsync(owner, request, transfer).ConfigureAwait(true);
                decisionTcs.TrySetResult(decision);
            }));

        var completed = await Task.WhenAny(decisionTcs.Task, Task.Delay(ConfirmTimeout)).ConfigureAwait(false);
        return completed == decisionTcs.Task
            ? await decisionTcs.Task.ConfigureAwait(false)
            : ExternalReceiveDecision.Declined;
    }

    /// <summary>弹接收确认窗（60 秒无操作由 DialogService 自动关闭），把用户选择映射为接收决策。</summary>
    private async Task<ExternalReceiveDecision> ShowConfirmDialogAsync(
        Window owner, ExternalReceiveRequest request, FileTransferInfo transfer)
    {
        try
        {
            var result = await _dialogs.ShowDialogAsync<ReceiveRequestViewModel>(new DialogParameters
            {
                [ReceiveRequestViewModel.DialogKeys.Transfer] = transfer,
                [ReceiveRequestViewModel.DialogKeys.SaveDirectory] = request.SaveDirectory,
                [ReceiveRequestViewModel.DialogKeys.SenderName] = request.SenderName
            }, new DialogOptions { Owner = owner, Timeout = ConfirmTimeout }).ConfigureAwait(false);

            if (result.Result != ButtonResult.Ok ||
                !result.Parameters.TryGetValue<ReceiveRequestResult?>(
                    ReceiveRequestViewModel.DialogKeys.Result, out var choice) ||
                choice is null)
            {
                return ExternalReceiveDecision.Declined;
            }

            if (choice.AlwaysAccept)
            {
                _settingsService.Update(s =>
                {
                    if (!s.IsFavorite(request.SenderId))
                    {
                        s.ToggleFavorite(request.SenderId);
                    }
                });
            }

            return new ExternalReceiveDecision(choice.Accept, choice.AlwaysAccept);
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
