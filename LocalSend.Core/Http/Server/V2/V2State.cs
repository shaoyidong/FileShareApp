using System.Net;
using LocalSend.Core.Util;

namespace LocalSend.Core.Http.Server.V2;

/// <summary>
/// v2 协议端点的运行时状态，对应 Rust 端 <c>src/http/server/mod.rs</c> 中的 <c>V2State</c>。
/// </summary>
/// <remarks>
/// 服务器同一时刻只允许一个上传会话：<see cref="Session"/> 槽位为 null 时表示空闲。
/// 多个并发请求需要先获取 <see cref="SessionLock"/>。
/// </remarks>
public sealed class V2State
{
    /// <summary>prepare-upload 必须提供的 PIN，可空。</summary>
    public string? Pin { get; }

    /// <summary>服务器事件输出通道（推送到应用）。</summary>
    public System.Threading.Channels.Channel<ServerEventV2> EventTx { get; }

    /// <summary>保护 <see cref="Session"/> 的锁。</summary>
    public SemaphoreSlim SessionLock { get; } = new(1, 1);

    /// <summary>当前会话槽位；null 表示空闲。</summary>
    public SessionStateV2? Session { get; set; }

    /// <summary>PIN 失败次数缓存（按 IP）。</summary>
    public SemaphoreSlim PinAttemptsLock { get; } = new(1, 1);

    public LruCache<IPAddress, uint> PinAttempts { get; } = new(200);

    public V2State(string? pin, System.Threading.Channels.Channel<ServerEventV2> eventTx)
    {
        Pin = pin;
        EventTx = eventTx;
    }
}
