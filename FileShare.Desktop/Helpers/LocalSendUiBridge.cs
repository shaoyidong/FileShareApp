using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FileShare.Core.Models;
using FileShare.Core.Services;
using CommunityToolkit.Mvvm.Messaging;
using Shyd.Prism.Localization;
using FileShare.Desktop.Services;
using Shyd.Prism.Notifications;
using Shyd.Prism.Dialogs;
using FileShare.Desktop.ViewModels;
using FileShare.LocalSend;
using System;
using System.Threading;
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
    private readonly IMessenger _messenger;
    private readonly IClassicDesktopStyleApplicationLifetime _appLifetime;
    private readonly SynchronizationContext _uiContext;

    public LocalSendUiBridge(
        IToastService toastService,
        IAppSettingsService settingsService,
        IDialogService dialogs,
        IMessenger messenger,
        IClassicDesktopStyleApplicationLifetime appLifetime,
        SynchronizationContext uiContext)
    {
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
        _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
    }

    /// <inheritdoc />
    public void ShowToast(string title, string message, Action? onClick = null) =>
        _toastService.Show(title, message, onClick,
            Loc.Get(onClick is null ? "6 秒后自动关闭" : "点击查看（6 秒后自动关闭）"));

    /// <inheritdoc />
    public void ShowTextMessage(string senderName, string message)
    {
        _uiContext.Post(_ =>
        {
            var owner = _appLifetime.MainWindow;
            if (owner is null)
            {
                return;
            }

            void ShowMessageCard()
            {
                _dialogs.Show<MessageReceivedViewModel>(new DialogParameters
                {
                    [MessageReceivedViewModel.DialogKeys.SenderName] = senderName,
                    [MessageReceivedViewModel.DialogKeys.Message] = message
                });
            }

            // IsActive 在主窗口被其他窗口遮挡时也是 false，不适合判断是否退到后台。
            // 托盘隐藏 → IsVisible=false（WindowState 可能仍是 Normal），最小化 → WindowState.Minimized。
            var isBackground = !owner.IsVisible || owner.WindowState == WindowState.Minimized;
            if (!isBackground)
            {
                ActivateWindow(owner);
                ShowMessageCard();
                return;
            }

            // 托盘 / 后台：只弹通知；点击通知才显示主窗口并弹消息卡片（与文件接收确认同策略）
            _toastService.Show(
                Loc.Get("收到文本消息"),
                message.Length > 60 ? message[..60] + "…" : message,
                () => _uiContext.Post(_ =>
                {
                    ActivateWindow(owner);
                    ShowMessageCard();
                }, null),
                Loc.Get("点击查看（6 秒后自动关闭）"));
        }, null);
    }

    /// <inheritdoc />
    public string GetText(string key) => Loc.Get(key);

    /// <inheritdoc />
    public void ReportTransfer(ExternalTransferUpdate update) =>
        _messenger.Send(update);

    /// <inheritdoc />
    public async Task<ExternalReceiveDecision> ConfirmReceiveAsync(ExternalReceiveRequest request)
    {
        var settings = _settingsService.Current;

        // 与 FileShare 自有协议一致：收藏设备 + 开启自动接收 → 免确认
        if (settings.AutoAcceptFromFavorites && settings.IsFavorite(request.SenderId))
        {
            return ExternalReceiveDecision.AcceptOnce;
        }

        var owner = await InvokeOnUiAsync(() => _appLifetime.MainWindow).ConfigureAwait(false);
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

        // 与文本消息弹窗同一套判定：IsActive 在主窗口只是被别的窗口遮挡时也是 false，
        // 用它判断会漏弹确认窗（只发一条 toast）。只有不可见 / 最小化才算后台。
        var isBackground = await InvokeOnUiAsync(
            () => !owner.IsVisible || owner.WindowState == WindowState.Minimized).ConfigureAwait(false);
        if (!isBackground)
        {
            await InvokeOnUiAsync(() => { ActivateWindow(owner); return true; }).ConfigureAwait(false);
            return await ShowConfirmDialogAsync(owner, request, transfer).ConfigureAwait(false);
        }

        // 托盘 / 后台：只弹通知；点击通知才显示主窗口并弹确认窗（60 秒总超时保留）
        var decisionTcs = new TaskCompletionSource<ExternalReceiveDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _toastService.Show(
            Loc.Get("收到文件传输请求"),
            Loc.F("{0} 想发送：{1}", request.SenderName, request.FileName),
            () => _uiContext.Post(async _ =>
            {
                ActivateWindow(owner);
                var decision = await ShowConfirmDialogAsync(owner, request, transfer).ConfigureAwait(true);
                decisionTcs.TrySetResult(decision);
            }, null),
            Loc.Get("点击查看（6 秒后自动关闭）"));

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

            // 接受（Ok）/拒绝（No）都携带选择结果；X 关闭 / 结果缺失按拒绝处理（与自有协议路径一致）
            if (result.Parameters.TryGetValue<ReceiveRequestResult?>(
                    ReceiveRequestViewModel.DialogKeys.Result, out var choice) &&
                choice is not null)
            {
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

            return ExternalReceiveDecision.Declined;
        }
        catch (Exception)
        {
            return ExternalReceiveDecision.Declined;
        }
    }

    /// <summary>把返回值/异常的读取投递到 UI 线程；已在该上下文时直接执行。</summary>
    private Task<T> InvokeOnUiAsync<T>(Func<T> func)
    {
        if (SynchronizationContext.Current == _uiContext)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
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
