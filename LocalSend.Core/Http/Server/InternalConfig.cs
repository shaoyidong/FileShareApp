using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;

namespace LocalSend.Core.Http.Server;

/// <summary>
/// 应用内部端点配置，对应 Rust 端 <c>src/http/server/internal.rs</c> 中的 <c>InternalConfig</c>。
/// </summary>
public sealed class InternalConfig
{
    /// <summary>show 路由必须携带的 token。</summary>
    public string ShowToken { get; set; } = string.Empty;

    /// <summary>内部事件输出通道。</summary>
    public Channel<InternalEvent> EventTx { get; set; } = Channel.CreateUnbounded<InternalEvent>();
}

/// <summary>应用内部事件，对应 Rust 端 <c>InternalEvent</c>。</summary>
public abstract class InternalEvent
{
    /// <summary>另一个程序实例请求当前实例显示自身。</summary>
    public sealed class Show : InternalEvent
    {
        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    }
}

/// <summary>
/// 应用内部端点的运行时状态，对应 Rust 端 <c>InternalState</c>。
/// </summary>
public sealed class InternalState
{
    public string ShowToken { get; }
    public Channel<InternalEvent> EventTx { get; }

    public InternalState(InternalConfig config)
    {
        ShowToken = config.ShowToken;
        EventTx = config.EventTx;
    }
}

/// <summary>
/// 应用内部端点的请求处理逻辑，对应 Rust 端 <c>src/http/server/internal.rs</c> 中的 <c>show</c>。
/// </summary>
internal sealed class InternalRoutes
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly InternalState _state;

    public InternalRoutes(InternalState state) { _state = state; }

    /// <summary>处理 <c>POST /api/localsend/v2/show?token=...</c>。</summary>
    public async Task<IHttpResponse> ShowAsync(
        Stream body, IReadOnlyDictionary<string, string> query, CancellationToken ct = default)
    {
        if (!query.TryGetValue("token", out var token) || token != _state.ShowToken)
        {
            throw AppError.WithMessage(HttpStatusCode.Forbidden, "Invalid token");
        }

        List<string> args = new();
        if (body.CanSeek && body.Length > 0)
        {
            try
            {
                var req = await JsonSerializer.DeserializeAsync<ShowRequest>(body, JsonOpts, ct).ConfigureAwait(false);
                if (req?.Args != null) args.AddRange(req.Args);
            }
            catch (JsonException)
            {
                throw AppError.BadRequest("Invalid JSON body");
            }
        }

        await _state.EventTx.Writer.WriteAsync(new InternalEvent.Show { Args = args }, ct).ConfigureAwait(false);
        return new EmptyResponse(HttpStatusCode.OK);
    }

    private sealed class ShowRequest
    {
        public List<string> Args { get; set; } = new();
    }
}
