using System;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>弹窗按钮结果（仿 Prism 的 ButtonResult）。</summary>
public enum ButtonResult
{
    /// <summary>无明确结果。</summary>
    None,

    /// <summary>确认 / 接收 / 发送。</summary>
    Ok,

    /// <summary>取消（含标题栏直接关闭）。</summary>
    Cancel,

    /// <summary>是。</summary>
    Yes,

    /// <summary>否 / 拒绝。</summary>
    No,
}

/// <summary>弹窗关闭时回传的结果。</summary>
public interface IDialogResult
{
    /// <summary>按钮结果（标题栏直接关闭为 <see cref="ButtonResult.Cancel"/>）。</summary>
    ButtonResult Result { get; }

    /// <summary>弹窗回传的数据。</summary>
    IDialogParameters Parameters { get; }

    /// <summary>弹窗过程中发生的异常（如有）。</summary>
    Exception? Exception { get; }
}

/// <inheritdoc />
public sealed class DialogResult : IDialogResult
{
    /// <inheritdoc />
    public ButtonResult Result { get; }

    /// <inheritdoc />
    public IDialogParameters Parameters { get; }

    /// <inheritdoc />
    public Exception? Exception { get; }

    /// <summary>创建一个弹窗结果。</summary>
    public DialogResult(ButtonResult result, IDialogParameters? parameters = null, Exception? exception = null)
    {
        Result = result;
        Parameters = parameters ?? new DialogParameters();
        Exception = exception;
    }
}
