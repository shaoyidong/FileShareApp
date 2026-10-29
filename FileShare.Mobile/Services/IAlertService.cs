using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Mobile.Services
{
    public interface IAlertService
    {
        // For alerts with "OK" only (no cancellation)
        Task DisplayAlertAsync(string title, string message, string accept);

        // For confirmation alerts (with "Accept" and "Cancel" buttons)
        Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel);

        Task DisplayToastAsync(string message, double textSize = 14);

        Task DisplaySnackbarAsync(string message, Action? action = null,
        string actionButtonText = "OK",
        TimeSpan? duration = null);

        Task<string> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons);

        /// <summary>弹出文本输入框（取消时返回 null）。</summary>
        Task<string?> DisplayPromptAsync(
            string title,
            string message,
            string accept = "OK",
            string cancel = "取消",
            string? placeholder = null,
            int maxLength = -1,
            Keyboard? keyboard = null);
    }
}