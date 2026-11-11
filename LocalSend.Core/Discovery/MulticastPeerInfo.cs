using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Model;
using System.Net;

namespace LocalSend.Core.Discovery;

/// <summary>
/// 通过 UDP 组播发现的对端设备信息。
/// </summary>
public sealed class MulticastPeerInfo
{
    public string Alias { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? DeviceModel { get; init; }
    public DeviceType? DeviceType { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public ushort Port { get; init; }
    public ProtocolTypeV2 Protocol { get; init; }
    public bool Download { get; init; }
    public IPAddress IP { get; init; } = IPAddress.None;

    public override string ToString() =>
        $"[{Alias}] {IP}:{Port} (v{Version}, {Protocol})";
}
