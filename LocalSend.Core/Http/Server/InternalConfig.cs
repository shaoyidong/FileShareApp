using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Json;

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
                var req = await LsJson.DeserializeAsync<ShowRequest>(body, ct).ConfigureAwait(false);
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
}

/// <summary>
/// <c>POST /api/localsend/v2/show</c> 的请求体。
/// 提到顶层是为了能在 <see cref="LocalSendJsonContext"/> 中注册（AOT 下源生成要求类型可访问）。
/// </summary>
public sealed class ShowRequest
{
    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();
}
