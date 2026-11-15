using FileShare.LocalSend;
using System;

namespace FileShare.Desktop.Services;

/// <summary>
/// 外部协议传输事件总线（事件聚合思想的落地）：LocalSend / 网页分享等协议适配层
/// 把传输状态发布到总线，主界面 ViewModel 订阅并同步到传输列表。
/// <para>解耦协议桥与主 ViewModel（直接注入会形成 MainViewModel → ILocalSendInteropHost
/// → LocalSendUiBridge → MainViewModel 的循环依赖）。</para>
/// </summary>
public interface ITransferEventBus
{
    /// <summary>外部协议传输更新事件（可能从后台线程触发，订阅方自行封送 UI 线程）。</summary>
    event Action<ExternalTransferUpdate>? ExternalTransferReceived;

    /// <summary>发布一条外部协议传输更新。</summary>
    void PublishExternalTransfer(ExternalTransferUpdate update);
}

/// <inheritdoc />
public sealed class TransferEventBus : ITransferEventBus
{
    /// <inheritdoc />
    public event Action<ExternalTransferUpdate>? ExternalTransferReceived;

    /// <inheritdoc />
    public void PublishExternalTransfer(ExternalTransferUpdate update) =>
        ExternalTransferReceived?.Invoke(update);
}
