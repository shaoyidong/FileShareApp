using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.State;
using LocalSend.Core.Model;
using LocalSend.Core.Util;

namespace LocalSend.Core.Http.Server.V2;

/// <summary>
/// v2.1 协议的请求处理逻辑，对应 Rust 端 <c>src/http/server/v2.rs</c>。
/// </summary>
/// <remarks>
/// 这一层只关心协议状态机（会话管理、PIN 校验、文件接收），
/// 具体的 HTTP 传输（HttpListener / Kestrel）由 <see cref="LocalSendServer"/> 适配。
/// </remarks>
public sealed class V2Routes
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly V2State _state;
    private readonly Func<ClientInfo> _getInfo;
    private readonly Func<bool> _hasWeb;

    public V2Routes(V2State state, Func<ClientInfo> getInfo, Func<bool> hasWeb)
    {
        _state = state;
        _getInfo = getInfo;
        _hasWeb = hasWeb;
    }

    /// <summary>
    /// 处理 <c>POST /api/localsend/v2/register</c>。
    /// </summary>
    public async Task<JsonResponse<RegisterResponseDtoV2>> RegisterAsync(
        Stream body, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        var payload = await ReadJsonAsync<RegisterDtoV2>(body, ct).ConfigureAwait(false);

        // TLS 下只信任带有效客户端证书的注册（指纹不可伪造）。
        bool fingerprintValid = clientInfo.CertFingerprint == null
            || payload.Fingerprint.ToUpperInvariant() == clientInfo.CertFingerprint;

        if (fingerprintValid)
        {
            await _state.EventTx.Writer.WriteAsync(
                new ServerEventV2.Register { Ip = clientInfo.Ip, Info = payload }, ct).ConfigureAwait(false);
        }

        var info = _getInfo();
        return new JsonResponse<RegisterResponseDtoV2>(HttpStatusCode.OK, new RegisterResponseDtoV2
        {
            Alias = info.Alias,
            Version = ProtocolVersionV2.Version,
            DeviceModel = info.DeviceModel,
            DeviceType = info.DeviceType,
            Fingerprint = info.Token,
            Download = _hasWeb()
        });
    }

    /// <summary>处理 <c>GET /api/localsend/v2/info</c>。</summary>
    public Task<JsonResponse<InfoResponseDtoV2>> InfoAsync(CancellationToken ct = default)
    {
        var info = _getInfo();
        return Task.FromResult(new JsonResponse<InfoResponseDtoV2>(HttpStatusCode.OK, new InfoResponseDtoV2
        {
            Alias = info.Alias,
            Version = ProtocolVersionV2.Version,
            DeviceModel = info.DeviceModel,
            DeviceType = info.DeviceType,
            Fingerprint = info.Token,
            Download = _hasWeb()
        }));
    }

    /// <summary>处理 <c>POST /api/localsend/v2/prepare-upload</c>。</summary>
    public async Task<IHttpResponse> PrepareUploadAsync(
        Stream body, IReadOnlyDictionary<string, string> query, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        await PinChecker.CheckPinAsync(_state.Pin, _state.PinAttemptsLock, _state.PinAttempts, query, clientInfo.Ip)
            .ConfigureAwait(false);

        var payload = await ReadJsonAsync<PrepareUploadRequestDtoV2>(body, ct).ConfigureAwait(false);
        if (payload.Files.Count == 0)
        {
            throw AppError.BadRequest("No files provided");
        }

        // 占用唯一的会话槽位。
        await _state.SessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.Session != null)
            {
                throw AppError.WithMessage(HttpStatusCode.Conflict, "Blocked by another session");
            }
            _state.Session = new SessionStateV2.PendingState();
        }
        finally
        {
            _state.SessionLock.Release();
        }

        var sessionId = Guid.NewGuid().ToString();
        // 用 try/finally 在异常 / 取消时清理 pending 槽位，对应 Rust 端 PendingSessionGuard。
        bool armed = true;
        try
        {
            var prepareEvent = new ServerEventV2.PrepareUpload
            {
                SessionId = sessionId,
                Ip = clientInfo.Ip,
                Info = payload.Info,
                CertFingerprint = clientInfo.CertFingerprint,
                Files = payload.Files
            };
            await _state.EventTx.Writer.WriteAsync(prepareEvent, ct).ConfigureAwait(false);

            var decision = await prepareEvent.DecisionTx.Task.ConfigureAwait(false);

            HashSet<string> acceptedIds = decision switch
            {
                PrepareUploadDecisionV2.Decline _ =>
                    throw AppError.WithMessage(HttpStatusCode.Forbidden, "Rejected"),
                PrepareUploadDecisionV2.Accept a => a.FileIds,
                _ => throw AppError.WithMessage(HttpStatusCode.InternalServerError, "Invalid decision")
            };

            var files = payload.Files
                .Where(kv => acceptedIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => new SessionFileV2
                {
                    Dto = kv.Value,
                    Token = Guid.NewGuid().ToString(),
                    Status = FileStatusV2.Pending
                });

            if (files.Count == 0)
            {
                await ClearPendingSessionAsync().ConfigureAwait(false);
                return new EmptyResponse(HttpStatusCode.NoContent);
            }

            var tokens = files.ToDictionary(kv => kv.Key, kv => kv.Value.Token);

            await _state.SessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _state.Session = new SessionStateV2.ActiveState
                {
                    Session = new UploadSessionV2
                    {
                        SessionId = sessionId,
                        SenderIp = clientInfo.Ip,
                        Files = files
                    }
                };
            }
            finally
            {
                _state.SessionLock.Release();
            }
            armed = false;

            return new JsonResponse<PrepareUploadResponseDtoV2>(HttpStatusCode.OK,
                new PrepareUploadResponseDtoV2 { SessionId = sessionId, Files = tokens });
        }
        finally
        {
            if (armed)
            {
                await ClearPendingSessionAsync().ConfigureAwait(false);
                // 通知应用：prepare-upload 已中止。
                await _state.EventTx.Writer.WriteAsync(
                    new ServerEventV2.PrepareUploadAborted { SessionId = sessionId }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>处理 <c>POST /api/localsend/v2/upload</c>。</summary>
    public async Task<IHttpResponse> UploadAsync(
        Stream body, IReadOnlyDictionary<string, string> query, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        if (!query.TryGetValue("sessionId", out var sessionId) ||
            !query.TryGetValue("fileId", out var fileId) ||
            !query.TryGetValue("token", out var token))
        {
            throw AppError.WithMessage(HttpStatusCode.BadRequest, "Missing parameters");
        }

        FileDto fileDto;
        await _state.SessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.Session is not SessionStateV2.ActiveState active)
            {
                throw InvalidTokenError();
            }
            var session = active.Session;
            if (session.SessionId != sessionId || !session.SenderIp.Equals(clientInfo.Ip))
            {
                throw InvalidTokenError();
            }
            if (!session.Files.TryGetValue(fileId, out var file))
            {
                throw InvalidTokenError();
            }
            if (file.Token != token || file.Status != FileStatusV2.Pending)
            {
                throw InvalidTokenError();
            }
            file.Status = FileStatusV2.InProgress;
            fileDto = file.Dto;
        }
        finally
        {
            _state.SessionLock.Release();
        }

        bool armed = true;
        try
        {
            var fileUploadEvent = new ServerEventV2.FileUpload
            {
                SessionId = sessionId,
                FileId = fileId,
                File = fileDto
            };
            await _state.EventTx.Writer.WriteAsync(fileUploadEvent, ct).ConfigureAwait(false);

            var target = await fileUploadEvent.TargetTx.Task.ConfigureAwait(false);

            bool success = await FileSaver.SaveReqToTargetAsync(body, target, fileDto.Size, ct).ConfigureAwait(false);
            await FinalizeFileAsync(sessionId, fileId, success).ConfigureAwait(false);
            armed = false;

            if (!success)
            {
                throw AppError.WithStatus(HttpStatusCode.InternalServerError);
            }
            return new EmptyResponse(HttpStatusCode.OK);
        }
        finally
        {
            if (armed)
            {
                await FinalizeFileAsync(sessionId, fileId, false).ConfigureAwait(false);
            }
        }
    }

    /// <summary>处理 <c>POST /api/localsend/v2/cancel</c>。</summary>
    public async Task<IHttpResponse> CancelAsync(
        IReadOnlyDictionary<string, string> query, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        if (query.TryGetValue("sessionId", out var sessionId))
        {
            bool cancelled;
            await _state.SessionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state.Session is SessionStateV2.ActiveState active
                    && active.Session.SessionId == sessionId
                    && active.Session.SenderIp.Equals(clientInfo.Ip))
                {
                    _state.Session = null;
                    cancelled = true;
                }
                else
                {
                    cancelled = false;
                }
            }
            finally
            {
                _state.SessionLock.Release();
            }

            if (cancelled)
            {
                await _state.EventTx.Writer.WriteAsync(
                    new ServerEventV2.SessionEnd { SessionId = sessionId, Reason = SessionEndReasonV2.Cancelled },
                    ct).ConfigureAwait(false);
            }
            else
            {
                // 不是我方的接收会话：可能是远端取消我方正在发起的发送会话。
                await _state.EventTx.Writer.WriteAsync(
                    new ServerEventV2.CancelReceived { Ip = clientInfo.Ip, SessionId = sessionId },
                    ct).ConfigureAwait(false);
            }
        }
        return new EmptyResponse(HttpStatusCode.OK);
    }

    /// <summary>把 pending 槽位清空（如果当前是 pending）。</summary>
    private async Task ClearPendingSessionAsync()
    {
        await _state.SessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state.Session is SessionStateV2.PendingState)
            {
                _state.Session = null;
            }
        }
        finally
        {
            _state.SessionLock.Release();
        }
    }

    /// <summary>标记文件最终状态；若所有文件都已结束则关闭会话并通知应用。</summary>
    private async Task FinalizeFileAsync(string sessionId, string fileId, bool success)
    {
        bool sessionEnded = false;
        await _state.SessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state.Session is not SessionStateV2.ActiveState active) return;
            var session = active.Session;
            if (session.SessionId != sessionId) return;

            if (session.Files.TryGetValue(fileId, out var file) && file.Status == FileStatusV2.InProgress)
            {
                file.Status = success ? FileStatusV2.Finished : FileStatusV2.Failed;
            }
            if (session.IsComplete())
            {
                _state.Session = null;
                sessionEnded = true;
            }
        }
        finally
        {
            _state.SessionLock.Release();
        }

        if (sessionEnded)
        {
            await _state.EventTx.Writer.WriteAsync(
                new ServerEventV2.SessionEnd { SessionId = sessionId, Reason = SessionEndReasonV2.Finished })
                .ConfigureAwait(false);
        }
    }

    private static AppError InvalidTokenError() =>
        AppError.WithMessage(HttpStatusCode.Forbidden, "Invalid token or IP address");

    private static async Task<T> ReadJsonAsync<T>(Stream body, CancellationToken ct)
    {
        try
        {
            var result = await JsonSerializer.DeserializeAsync<T>(body, JsonOpts, ct).ConfigureAwait(false);
            return result ?? throw AppError.BadRequest("Invalid JSON body");
        }
        catch (JsonException)
        {
            throw AppError.BadRequest("Invalid JSON body");
        }
    }
}

/// <summary>请求中的客户端信息，对应 Rust 端 <c>RequestClientInfo</c>。</summary>
public sealed class RequestClientInfo
{
    public IPAddress Ip { get; init; } = IPAddress.None;
    /// <summary>客户端证书 DER 字节（仅 TLS）。</summary>
    public byte[]? Cert { get; init; }

    /// <summary>证书指纹（SHA-256 大写十六进制）；非 TLS 时为 null。</summary>
    public string? CertFingerprint =>
        Cert == null ? null : LocalSend.Core.Crypto.CertVerifier.FingerprintFromCertDer(Cert);
}

/// <summary>轻量的 HTTP 响应抽象，便于在不同的 HTTP 后端之间复用。</summary>
public interface IHttpResponse
{
    HttpStatusCode Status { get; }
    string? ContentType { get; }
    byte[]? Body { get; }
    /// <summary>额外的响应头（key, value）。</summary>
    IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; }
}

/// <summary>JSON 响应，对应 Rust 端 <c>JsonResponse&lt;T&gt;</c>。</summary>
public sealed class JsonResponse<T> : IHttpResponse
{
    public HttpStatusCode Status { get; }
    public T Body { get; }
    public string? ContentType => "application/json";
    byte[]? IHttpResponse.Body => _body ??= JsonSerializer.SerializeToUtf8Bytes(Body, JsonOpts);
    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = Array.Empty<(string, string)>();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private byte[]? _body;

    public JsonResponse(HttpStatusCode status, T body)
    {
        Status = status;
        Body = body;
    }
}

/// <summary>空响应体。</summary>
public sealed class EmptyResponse : IHttpResponse
{
    public HttpStatusCode Status { get; }
    public string? ContentType => null;
    public byte[]? Body => null;
    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = Array.Empty<(string, string)>();
    public EmptyResponse(HttpStatusCode status) { Status = status; }
}
