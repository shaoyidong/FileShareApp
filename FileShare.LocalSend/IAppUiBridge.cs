using System;
using System.Threading.Tasks;

namespace FileShare.LocalSend;

/// <summary>
/// 协议适配层 ↔ 桌面 UI 的最小契约：通知、文案、接收确认、传输列表同步。
/// <para>适配层不引用 Avalonia；桌面端实现此接口，移动/其它宿主可自行实现。</para>
/// </summary>
public interface IAppUiBridge
{
    /// <summary>
    /// 显示一条通知（实现方负责切回 UI 线程）。
    /// <paramref name="onClick"/> 为点击通知时的回调（用于「点击通知才弹确认窗」）。
    /// </summary>
    void ShowToast(string title, string message, Action? onClick = null);

    /// <summary>
    /// 收到文本消息：弹出展示窗口，显示全文并提供「复制全文」按钮。
    /// 实现方负责切回 UI 线程；无界面宿主可忽略。
    /// </summary>
    void ShowTextMessage(string senderName, string message);

    /// <summary>按中文原文取当前语言文案；未翻译时返回原文。</summary>
    string GetText(string key);

    /// <summary>
    /// 询问用户是否接收。收藏设备且开启「自动接收收藏设备」时应直接返回接受，不弹窗。
    /// </summary>
    Task<ExternalReceiveDecision> ConfirmReceiveAsync(ExternalReceiveRequest request);

    /// <summary>把外部传输的新建/进度/完成状态同步到主界面传输列表。</summary>
    void ReportTransfer(ExternalTransferUpdate update);
}
