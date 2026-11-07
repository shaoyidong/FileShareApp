namespace FileShare.LocalSend;

/// <summary>
/// 适配层反向依赖 UI 的最小契约：通知 + 文案本地化。
/// <para>
/// 由宿主（Desktop / 未来的 Mobile）实现并注入，使 FileShare.LocalSend 不需要引用
/// Avalonia、IToastService 或具体的本地化实现，保持纯协议层。
/// </para>
/// </summary>
public interface ILocalSendUiBridge
{
    /// <summary>显示一条通知（实现方负责切回 UI 线程）。</summary>
    void ShowToast(string title, string message);

    /// <summary>按中文原文取当前语言文案；未翻译时返回原文。</summary>
    string GetText(string key);
}
