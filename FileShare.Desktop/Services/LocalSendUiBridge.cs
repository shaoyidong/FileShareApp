using System;
using FileShare.Desktop.Localization;
using FileShare.LocalSend;

namespace FileShare.Desktop.Services;

/// <summary>
/// <see cref="ILocalSendUiBridge"/> 的桌面端实现：把协议适配层的通知与文案请求
/// 转给桌面端的 <see cref="IToastService"/> 与 <see cref="Loc"/>。
/// </summary>
public sealed class LocalSendUiBridge : ILocalSendUiBridge
{
    private readonly IToastService _toastService;

    public LocalSendUiBridge(IToastService toastService)
    {
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
    }

    /// <inheritdoc />
    public void ShowToast(string title, string message) => _toastService.Show(title, message);

    /// <inheritdoc />
    public string GetText(string key) => Loc.Get(key);
}
