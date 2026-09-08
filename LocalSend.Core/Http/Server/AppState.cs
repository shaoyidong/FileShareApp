using System.Threading.Channels;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.Server.V3;
using LocalSend.Core.Http.Server.Web;
using LocalSend.Core.Http.State;

namespace LocalSend.Core.Http.Server;

/// <summary>
/// 服务端应用状态，对应 Rust 端 <c>src/http/server/mod.rs</c> 中的 <c>AppState</c>。
/// </summary>
/// <remarks>
/// 这一层把多个子模块的状态聚合在一起：
/// <list type="bullet">
///   <item><see cref="Info"/>：本机设备信息（可变，受 <see cref="InfoLock"/> 保护）。</item>
///   <item><see cref="Web"/>：Web 发送（Download API）。</item>
///   <item><see cref="Internal"/>：应用内部端点。</item>
///   <item><see cref="V2"/>：v2 协议端点（可空，表示未启用）。</item>
///   <item><see cref="V3"/>：v3 协议端点（始终启用）。</item>
/// </list>
/// </remarks>
public sealed class AppState
{
    public SemaphoreSlim InfoLock { get; } = new(1, 1);
    public ClientInfo Info { get; set; }

    public WebPageState? Web { get; }
    public InternalState? Internal { get; }
    public V2State? V2 { get; }
    public V3Routes V3 { get; }

    public AppState(
        ClientInfo info,
        InternalConfig? internalConfig,
        ServerConfigV2? v2Config,
        WebSendConfig? webSendConfig)
    {
        Info = info;
        V2 = v2Config != null ? new V2State(v2Config.Pin, v2Config.EventTx) : null;
        Web = webSendConfig != null ? new WebPageState(webSendConfig) : null;
        Internal = internalConfig != null ? new InternalState(internalConfig) : null;
        V3 = new V3Routes(() =>
        {
            InfoLock.Wait();
            try { return Info; }
            finally { InfoLock.Release(); }
        }, () => Web != null);
    }

    public ClientInfo SnapshotInfo()
    {
        InfoLock.Wait();
        try { return Info; }
        finally { InfoLock.Release(); }
    }
}
