using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.State;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Server.Web;

/// <summary>
/// Web 发送（Download API）的请求处理逻辑，对应 Rust 端 <c>src/http/server/web.rs</c>。
/// </summary>
public sealed class WebRoutes
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly WebPageState _state;
    private readonly Func<ClientInfo> _getInfo;

    public WebRoutes(WebPageState state, Func<ClientInfo> getInfo)
    {
        _state = state;
        _getInfo = getInfo;
    }

    /// <summary>处理 <c>POST /api/localsend/v2/prepare-download</c>。</summary>
    public async Task<IHttpResponse> PrepareDownloadAsync(
        IReadOnlyDictionary<string, string> query, string? userAgent, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        // 已接受客户端可重新拉取文件列表（例如刷新页面）。
        if (query.TryGetValue("sessionId", out var existingSessionId))
        {
            bool valid;
            await _state.SessionsLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                valid = _state.Sessions.TryGetValue(existingSessionId, out var s)
                    && s.Accepted && s.Ip.Equals(clientInfo.Ip);
            }
            finally { _state.SessionsLock.Release(); }
            if (valid)
            {
                return await FileListResponseAsync(existingSessionId).ConfigureAwait(false);
            }
        }

        await PinChecker.CheckPinAsync(_state.Pin, _state.PinAttemptsLock, _state.PinAttempts, query, clientInfo.Ip)
            .ConfigureAwait(false);

        // 每个 IP 一个会话；新请求会替换之前的会话。
        var sessionId = clientInfo.Ip.ToString();
        await _state.SessionsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.Sessions[sessionId] = new WebSendSession { Ip = clientInfo.Ip, Accepted = false };
        }
        finally { _state.SessionsLock.Release(); }

        bool armed = true;
        try
        {
            var ev = new WebSendEvent.PrepareDownload
            {
                Ip = clientInfo.Ip,
                SessionId = sessionId,
                UserAgent = userAgent
            };
            await _state.EventTx.Writer.WriteAsync(ev, ct).ConfigureAwait(false);

            bool accepted = await ev.DecisionTx.Task.ConfigureAwait(false);
            if (!accepted)
            {
                await ClearPendingSessionAsync(sessionId).ConfigureAwait(false);
                throw AppError.WithMessage(HttpStatusCode.Forbidden, "File transfer rejected.");
            }

            await _state.SessionsLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state.Sessions.TryGetValue(sessionId, out var s))
                {
                    s.Accepted = true;
                }
            }
            finally { _state.SessionsLock.Release(); }
            armed = false;

            return await FileListResponseAsync(sessionId).ConfigureAwait(false);
        }
        finally
        {
            if (armed)
            {
                await ClearPendingSessionAsync(sessionId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>处理 <c>GET /api/localsend/v2/download</c>。</summary>
    public async Task<IHttpResponse> DownloadAsync(
        IReadOnlyDictionary<string, string> query, RequestClientInfo clientInfo, CancellationToken ct = default)
    {
        if (!query.TryGetValue("sessionId", out var sessionId))
        {
            throw AppError.BadRequest("Missing sessionId.");
        }

        bool valid;
        await _state.SessionsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            valid = _state.Sessions.TryGetValue(sessionId, out var s)
                && s.Accepted && s.Ip.Equals(clientInfo.Ip);
        }
        finally { _state.SessionsLock.Release(); }
        if (!valid)
        {
            throw AppError.WithMessage(HttpStatusCode.Forbidden, "Invalid sessionId.");
        }

        if (!query.TryGetValue("fileId", out var fileId))
        {
            throw AppError.BadRequest("Missing fileId.");
        }
        if (!_state.Files.TryGetValue(fileId, out var file))
        {
            throw AppError.WithMessage(HttpStatusCode.Forbidden, "Invalid fileId.");
        }

        var ev = new WebSendEvent.FileDownload
        {
            SessionId = sessionId,
            FileId = fileId,
            File = file
        };
        await _state.EventTx.Writer.WriteAsync(ev, ct).ConfigureAwait(false);

        var content = await ev.ContentTx.Task.ConfigureAwait(false);
        var channel = content.IntoReceiver();
        var stream = new ChannelStream(channel);

        // 文件名中可能含 '/'（目录结构），替换为 '-'。
        var fileName = file.FileName.Replace('/', '-');
        // Content-Disposition 中的文件名做百分号编码，对应 Rust 端 utf8_percent_encode。
        var encodedFileName = Uri.EscapeDataString(fileName);

        return new StreamResponse(stream, HttpStatusCode.OK)
        {
            ContentType = "application/octet-stream",
            ContentDisposition = $"attachment; filename=\"{encodedFileName}\"",
            ContentLength = (long?)file.Size
        };
    }

    /// <summary>返回 prepare-download 的文件列表 JSON。</summary>
    private async Task<IHttpResponse> FileListResponseAsync(string sessionId)
    {
        var info = _getInfo();
        var body = new PrepareDownloadResponseDtoV2
        {
            Info = new InfoResponseDtoV2
            {
                Alias = info.Alias,
                Version = ProtocolVersionV2.Version,
                DeviceModel = info.DeviceModel,
                DeviceType = info.DeviceType,
                Fingerprint = info.Token,
                Download = true
            },
            SessionId = sessionId,
            Files = _state.Files
        };
        await Task.CompletedTask;
        return new JsonResponse<PrepareDownloadResponseDtoV2>(HttpStatusCode.OK, body);
    }

    private async Task ClearPendingSessionAsync(string sessionId)
    {
        await _state.SessionsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state.Sessions.TryGetValue(sessionId, out var s) && !s.Accepted)
            {
                _state.Sessions.Remove(sessionId);
            }
        }
        finally { _state.SessionsLock.Release(); }
    }
}

/// <summary>把 <see cref="Channel{Byte}"/> 适配为可读流，供响应体使用。</summary>
internal sealed class ChannelStream : Stream
{
    private readonly Channel<byte[]> _channel;
    private readonly ChannelReader<byte[]> _reader;
    private byte[] _current = Array.Empty<byte>();
    private int _offset;

    public ChannelStream(Channel<byte[]> channel)
    {
        _channel = channel;
        _reader = channel.Reader;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (_offset >= _current.Length)
        {
            if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
            if (!_reader.TryRead(out _current))
            {
                continue;
            }
            _offset = 0;
        }
        int n = Math.Min(count, _current.Length - _offset);
        Buffer.BlockCopy(_current, _offset, buffer, offset, n);
        _offset += n;
        return n;
    }
}

/// <summary>流式响应体，对应 Rust 端的 StreamBody。</summary>
public sealed class StreamResponse : IHttpResponse
{
    public HttpStatusCode Status { get; }
    public Stream Stream { get; }
    public string? ContentType { get; init; }
    public string? ContentDisposition { get; init; }
    public long? ContentLength { get; init; }
    byte[]? IHttpResponse.Body => null;

    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = new List<(string, string)>();

    public StreamResponse(Stream stream, HttpStatusCode status)
    {
        Stream = stream;
        Status = status;
    }
}
