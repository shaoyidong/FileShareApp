namespace FileShare.LocalSend;

/// <summary>
/// LocalSend 适配层使用的 UI 桥（保留此别名以兼容既有注入点）。
/// 通知 / 文案 / 接收确认 / 传输列表同步的通用能力定义在 <see cref="IAppUiBridge"/>。
/// </summary>
public interface ILocalSendUiBridge : IAppUiBridge
{
}
