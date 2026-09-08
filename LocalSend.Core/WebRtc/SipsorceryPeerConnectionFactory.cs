#if SIPSORCERY
using SIPSorcery.Net;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// 基于 SIPSorcery 的 <see cref="IRtcPeerConnectionFactory"/> 实现。
/// </summary>
/// <remarks>
/// 需要在 csproj 中添加 SIPSorcery 包引用并定义 SIPSORCERY 编译符号才能使用。
/// </remarks>
public sealed class SipsorceryPeerConnectionFactory : IRtcPeerConnectionFactory
{
    /// <summary>
    /// 默认 STUN 服务器列表，与 LocalSend 桌面端一致。
    /// </summary>
    private static readonly string[] DefaultStunServers =
    {
        "stun:stun.l.google.com:19302"
    };

    private readonly string[] _defaultStunServers;

    /// <summary>
    /// 使用默认 STUN 服务器（Google）初始化。
    /// </summary>
    public SipsorceryPeerConnectionFactory()
    {
        _defaultStunServers = DefaultStunServers;
    }

    /// <summary>
    /// 使用自定义默认 STUN 服务器列表初始化。
    /// </summary>
    public SipsorceryPeerConnectionFactory(IEnumerable<string> defaultStunServers)
    {
        _defaultStunServers = defaultStunServers.ToArray();
    }

    /// <inheritdoc />
    public IRtcPeerConnection Create(IReadOnlyList<string> stunServers)
    {
        var servers = stunServers.Count > 0
            ? stunServers
            : _defaultStunServers;

        var config = new RTCConfiguration
        {
            iceServers = servers.Select(u => new RTCIceServer { urls = u }).ToList()
        };

        var inner = new RTCPeerConnection(config);
        return new SipsorceryPeerConnection(inner);
    }
}
#endif