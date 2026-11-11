using LocalSend.Core.Http.DtoV2;

namespace FileShare.LocalSend;

/// <summary>
/// 通过 LocalSend 协议发现的设备（第三方客户端）。
/// </summary>
public sealed record LocalSendPeer(
    string DeviceId,
    string Alias,
    string IpAddress,
    ushort Port,
    ProtocolTypeV2 Protocol,
    string Fingerprint,
    global::LocalSend.Core.Model.DeviceType DeviceType = global::LocalSend.Core.Model.DeviceType.Desktop);

/// <summary>发送结果。</summary>
public sealed record LocalSendSendResult(bool Success, string? Error);
