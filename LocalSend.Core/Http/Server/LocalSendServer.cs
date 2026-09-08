using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.Server.V3;
using LocalSend.Core.Http.Server.Web;
using LocalSend.Core.Http.State;

namespace LocalSend.Core.Http.Server;

/// <summary>
/// LocalSend HTTP 服务器，对应 Rust 端 <c>src/http/server/mod.rs</c> 中的
/// <c>start_with_port</c> / <c>serve_connection</c> / <c>handle_request</c>。
/// </summary>
/// <remarks>
/// 基于 <see cref="TcpListener"/> 实现，可绑定 0.0.0.0 而无需管理员权限，
/// 使服务可被局域网内其他设备访问。实际生产环境可换成 Kestrel 以获得
/// 更精细的 mTLS 控制和更好的性能。
/// </remarks>
public sealed class LocalSendServer : IDisposable
{
    private readonly ushort _port;
    private readonly TlsConfig? _tls;
    private readonly AppState _state;
    private readonly V2Routes? _v2Routes;
    private readonly V3Routes _v3Routes;
    private readonly WebRoutes? _webRoutes;
    private readonly InternalRoutes? _internalRoutes;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public LocalSendServer(
        ushort port,
        TlsConfig? tlsConfig,
        ClientInfo info,
        InternalConfig? internalConfig = null,
        ServerConfigV2? v2Config = null,
        WebSendConfig? webSendConfig = null)
    {
        _port = port;
        _tls = tlsConfig;
        _state = new AppState(info, internalConfig, v2Config, webSendConfig);
        _v2Routes = _state.V2 != null ? new V2Routes(_state.V2, () => _state.SnapshotInfo(), () => _state.Web != null) : null;
        _v3Routes = _state.V3;
        _webRoutes = _state.Web != null ? new WebRoutes(_state.Web, () => _state.SnapshotInfo()) : null;
        _internalRoutes = _state.Internal != null ? new InternalRoutes(_state.Internal) : null;
    }

    /// <summary>
    /// 暴露内部状态，便于应用程序在收到事件后做交互
    /// （对应 Rust 端 <c>AppState</c> 通过扩展传递的模式）。
    /// </summary>
    public AppState State => _state;

    /// <summary>
    /// 启动 HTTP 服务器，返回可等待的 <see cref="Task"/>（在 <see cref="StopAsync"/> 后完成）。
    /// 对应 Rust 端 <c>start_with_port</c>。
    /// </summary>
    /// <remarks>
    /// 使用 <see cref="TcpListener"/> 绑定 0.0.0.0:{port}，无需管理员权限，
    /// 局域网内其他设备可通过本机 IP 直接访问。
    /// </remarks>
    public async Task StartAsync(CancellationToken externalCancel = default)
    {
        if (_listener != null)
        {
            throw new InvalidOperationException("Server already started");
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancel);

        // TcpListener 绑定 0.0.0.0，无需管理员权限，局域网内其他设备可直接访问
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"    [服务器] 监听 0.0.0.0:{_port}（局域网可访问）");

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (_cts.IsCancellationRequested)
                {
                    break;
                }

                // 每个连接独立处理，不阻塞 accept 循环。
                _ = Task.Run(() => HandleTcpClientAsync(client, _cts.Token), _cts.Token);
            }
        }
        finally
        {
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>停止服务器并释放监听端口。</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_listener != null)
        {
            try { _listener.Stop(); } catch { }
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _listener?.Server?.Dispose();
    }

    // -----------------------------------------------------------------------
    // HTTP 请求解析与响应写入（替代 HttpListener）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 处理单个 TCP 连接：解析 HTTP 请求 → 路由分发 → 写 HTTP 响应。
    /// </summary>
    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();

            // 1. 读取 HTTP 请求头（直到 \r\n\r\n）
            var (headerData, headerEnd, extraBodyBytes) = await ReadHeadersAsync(stream, ct);
            if (headerEnd < 0) return; // 连接关闭或格式错误

            // 2. 解析请求行和头部
            var headerText = Encoding.ASCII.GetString(headerData, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrEmpty(lines[0])) return;

            var firstLineParts = lines[0].Split(' ');
            if (firstLineParts.Length < 2) return;
            var method = firstLineParts[0];
            var rawPath = firstLineParts[1];

            // 解析 path 和 query string
            string path, queryString;
            var qIdx = rawPath.IndexOf('?');
            if (qIdx >= 0)
            {
                path = rawPath.Substring(0, qIdx);
                queryString = rawPath.Substring(qIdx + 1);
            }
            else
            {
                path = rawPath;
                queryString = "";
            }
            var query = QueryParser.ParseQuery(queryString);

            // 解析 headers
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }
            }

            // 3. 读取请求体
            // 支持 Content-Length（定长）和 Transfer-Encoding: chunked（分块，HttpClient 流式上传默认用此方式）
            Stream body = Stream.Null;
            long contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var clStr) && long.TryParse(clStr, out contentLength) && contentLength > 0)
            {
                var bodyBytes = new byte[contentLength];
                // 先拷贝已读取的多余字节（在 headerData 中 \r\n\r\n 之后的部分）
                var alreadyRead = extraBodyBytes;
                if (alreadyRead > 0)
                {
                    var copyLen = Math.Min(alreadyRead, contentLength);
                    Array.Copy(headerData, headerEnd + 4, bodyBytes, 0, copyLen);
                }
                var offset = Math.Min(alreadyRead, contentLength);
                while (offset < contentLength)
                {
                    var n = await stream.ReadAsync(bodyBytes.AsMemory((int)offset, (int)(contentLength - offset)), ct);
                    if (n == 0) break;
                    offset += n;
                }
                body = new MemoryStream(bodyBytes, 0, (int)offset);
            }
            else if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                // 分块传输编码：对端（HttpClient 流式上传）无 Content-Length，
                // 需按 chunked 协议解码。对应原版 Rust hyper 自动处理的逻辑。
                body = await ReadChunkedBodyAsync(stream, headerData, headerEnd, extraBodyBytes, ct);
            }

            var clientInfo = new RequestClientInfo
            {
                Ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None,
                Cert = null
            };

            // 4. 路由分发
            IHttpResponse response;
            try
            {
                response = await DispatchAsync(method, path, query, body, headers, clientInfo, ct).ConfigureAwait(false);
            }
            catch (AppError ae)
            {
                response = new BytesResponse("application/json", ae.ToJsonResponseBytes(), ae.Status);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[服务器] 处理请求异常: {ex}");
                response = new EmptyResponse(HttpStatusCode.InternalServerError);
            }
            finally
            {
                if (body != Stream.Null) body.Dispose();
            }

            // 5. 写 HTTP 响应
            await WriteTcpResponseAsync(stream, response, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[服务器] 连接处理异常: {ex}");
        }
    }

    /// <summary>
    /// 从网络流读取 HTTP 请求头，直到遇到 \r\n\r\n。
    /// 返回 (完整缓冲区, 头部结束位置, 额外的 body 字节数)。
    /// </summary>
    private static async Task<(byte[] data, int headerEnd, int extra)> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            ms.Write(buffer, 0, n);

            // 在已累积的数据中搜索 \r\n\r\n
            var data = ms.ToArray();
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0x0D && data[i + 1] == 0x0A && data[i + 2] == 0x0D && data[i + 3] == 0x0A)
                {
                    return (data, i, data.Length - (i + 4));
                }
            }

            // 防止过大的请求头
            if (ms.Length > 65536) break;
        }

        return (ms.ToArray(), -1, 0);
    }

    /// <summary>
    /// 解码 HTTP chunked transfer-encoding 请求体，返回完整 body 流。
    /// 对端流式上传（HttpClient + StreamContent）无 Content-Length，使用 chunked 编码；
    /// 原 Rust 版由 hyper 自动解码，此处手写解码以适配 TcpListener 实现。
    /// </summary>
    /// <param name="network">头部读取后的网络流。</param>
    /// <param name="headerData">ReadHeadersAsync 返回的完整缓冲区。</param>
    /// <param name="headerEnd">头部结束位置（\r\n\r\n 起始）。</param>
    /// <param name="extra">缓冲区中 \r\n\r\n 之后已预读的 body 字节数。</param>
    private static async Task<Stream> ReadChunkedBodyAsync(
        Stream network, byte[] headerData, int headerEnd, int extra, CancellationToken ct)
    {
        var outMs = new MemoryStream();
        int prefixPos = headerEnd + 4;
        int prefixEnd = headerEnd + 4 + extra;
        var netBuf = new byte[8192];
        int netPos = 0, netLen = 0;

        // 逐字节读：先消费预读缓冲，再从网络流按缓冲块读取
        async Task<int> ReadByteAsync()
        {
            if (prefixPos < prefixEnd) return headerData[prefixPos++];
            if (netPos < netLen) return netBuf[netPos++];
            netLen = await network.ReadAsync(netBuf, ct).ConfigureAwait(false);
            netPos = 0;
            if (netLen == 0) return -1;
            return netBuf[netPos++];
        }

        // 读一行（到 \r\n）
        async Task<string> ReadLineAsync()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = await ReadByteAsync();
                if (b < 0) break;
                if (b == '\r')
                {
                    int b2 = await ReadByteAsync();
                    if (b2 == '\n') break;
                    sb.Append((char)b);
                    if (b2 >= 0) sb.Append((char)b2);
                    continue;
                }
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        while (true)
        {
            var line = await ReadLineAsync();
            // chunk 大小行可能带 chunk-ext（;key=val），取分号前部分
            var sizeStr = line.Split(';')[0].Trim();
            if (!int.TryParse(sizeStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkSize) || chunkSize == 0)
            {
                // 最后一个 chunk（size=0），消费尾随空行
                await ReadLineAsync();
                break;
            }
            var chunk = new byte[chunkSize];
            int got = 0;
            while (got < chunkSize)
            {
                int b = await ReadByteAsync();
                if (b < 0) break;
                chunk[got++] = (byte)b;
            }
            outMs.Write(chunk, 0, got);
            await ReadLineAsync(); // chunk 数据后的 \r\n
        }
        outMs.Position = 0;
        return outMs;
    }

    /// <summary>
    /// 将 HTTP 响应写入网络流。
    /// </summary>
    private static async Task WriteTcpResponseAsync(Stream stream, IHttpResponse response, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {(int)response.Status} {response.Status}\r\n");

        if (response.ContentType != null)
            sb.Append($"Content-Type: {response.ContentType}\r\n");

        foreach (var (k, v) in response.ExtraHeaders)
            sb.Append($"{k}: {v}\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());

        switch (response)
        {
            case StreamResponse sr:
                {
                    var sb2 = new StringBuilder();
                    if (sr.ContentLength.HasValue)
                        sb2.Append($"Content-Length: {sr.ContentLength.Value}\r\n");
                    if (sr.ContentDisposition != null)
                        sb2.Append($"Content-Disposition: {sr.ContentDisposition}\r\n");
                    sb2.Append("Connection: close\r\n\r\n");

                    await stream.WriteAsync(headerBytes, ct);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(sb2.ToString()), ct);
                    await sr.Stream.CopyToAsync(stream, ct);
                    sr.Stream.Dispose();
                    break;
                }
            default:
                {
                    var sb2 = new StringBuilder();
                    if (response.Body != null && response.Body.Length > 0)
                    {
                        sb2.Append($"Content-Length: {response.Body.Length}\r\n");
                        sb2.Append("Connection: close\r\n\r\n");
                        await stream.WriteAsync(headerBytes, ct);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb2.ToString()), ct);
                        await stream.WriteAsync(response.Body, ct);
                    }
                    else
                    {
                        sb2.Append("Content-Length: 0\r\n");
                        sb2.Append("Connection: close\r\n\r\n");
                        await stream.WriteAsync(headerBytes, ct);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb2.ToString()), ct);
                    }
                    break;
                }
        }
    }

    // -----------------------------------------------------------------------
    // 路由分发（与原 HttpListener 版本逻辑完全一致）
    // -----------------------------------------------------------------------

    /// <summary>路由分发，对应 Rust 端 match 块。</summary>
    private async Task<IHttpResponse> DispatchAsync(
        string method,
        string path,
        Dictionary<string, string> query,
        Stream body,
        IReadOnlyDictionary<string, string> headers,
        RequestClientInfo clientInfo,
        CancellationToken ct)
    {
        if (method == "GET" && path == "/")
        {
            return WebIndex();
        }
        if (method == "GET" && path == "/main.js")
        {
            return WebMainJs();
        }
        if (method == "GET" && path == "/i18n.json")
        {
            return WebI18n();
        }

        if (method == "POST" && path == "/api/localsend/v2/prepare-download")
        {
            if (_webRoutes == null) return NotFound();
            headers.TryGetValue("User-Agent", out var ua);
            return await _webRoutes.PrepareDownloadAsync(query, ua, clientInfo, ct).ConfigureAwait(false);
        }
        if (method == "GET" && path == "/api/localsend/v2/download")
        {
            if (_webRoutes == null) return NotFound();
            return await _webRoutes.DownloadAsync(query, clientInfo, ct).ConfigureAwait(false);
        }

        if (method == "POST" && path == "/api/localsend/v2/register")
        {
            if (_v2Routes == null) return NotFound();
            return await _v2Routes.RegisterAsync(body, clientInfo, ct).ConfigureAwait(false);
        }
        if (method == "GET" && path == "/api/localsend/v2/info")
        {
            if (_v2Routes == null) return NotFound();
            return await _v2Routes.InfoAsync(ct).ConfigureAwait(false);
        }
        if (method == "POST" && path == "/api/localsend/v2/prepare-upload")
        {
            if (_v2Routes == null) return NotFound();
            return await _v2Routes.PrepareUploadAsync(body, query, clientInfo, ct).ConfigureAwait(false);
        }
        if (method == "POST" && path == "/api/localsend/v2/upload")
        {
            if (_v2Routes == null) return NotFound();
            return await _v2Routes.UploadAsync(body, query, clientInfo, ct).ConfigureAwait(false);
        }
        if (method == "POST" && path == "/api/localsend/v2/cancel")
        {
            if (_v2Routes == null) return NotFound();
            return await _v2Routes.CancelAsync(query, clientInfo, ct).ConfigureAwait(false);
        }

        if (method == "POST" && path == "/api/localsend/v2/show")
        {
            if (_internalRoutes == null) return NotFound();
            return await _internalRoutes.ShowAsync(body, query, ct).ConfigureAwait(false);
        }

        if (method == "POST" && path == "/api/localsend/v3/nonce")
        {
            return await _v3Routes.NonceExchangeAsync(body, clientInfo, ct).ConfigureAwait(false);
        }
        if (method == "POST" && path == "/api/localsend/v3/register")
        {
            return await _v3Routes.RegisterAsync(body, clientInfo, ct).ConfigureAwait(false);
        }

        return NotFound();
    }

    private static IHttpResponse NotFound() => new EmptyResponse(HttpStatusCode.NotFound);

    /// <summary>Web 首页，对应 Rust 端 <c>web::index</c>。</summary>
    private IHttpResponse WebIndex()
    {
        if (_state.Web == null)
        {
            return new EmptyResponse(HttpStatusCode.Forbidden);
        }
        return new TextResponse("text/html; charset=utf-8", WebAssets.IndexHtml);
    }

    /// <summary>Web 主 JS，对应 Rust 端 <c>web::main_js</c>。</summary>
    private IHttpResponse WebMainJs()
    {
        if (_state.Web == null)
        {
            return new EmptyResponse(HttpStatusCode.Forbidden);
        }
        return new TextResponse("text/javascript; charset=utf-8", WebAssets.MainJs);
    }

    /// <summary>Web i18n 文案，对应 Rust 端 <c>web::i18n</c>。</summary>
    private IHttpResponse WebI18n()
    {
        if (_state.Web == null)
        {
            return new EmptyResponse(HttpStatusCode.Forbidden);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_state.Web.I18n);
        return new BytesResponse("application/json", bytes);
    }
}

/// <summary>纯文本响应。</summary>
internal sealed class TextResponse : IHttpResponse
{
    public HttpStatusCode Status { get; } = HttpStatusCode.OK;
    public string? ContentType { get; }
    public byte[]? Body { get; }
    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = Array.Empty<(string, string)>();
    public TextResponse(string contentType, string content)
    {
        ContentType = contentType;
        Body = Encoding.UTF8.GetBytes(content);
    }
}

/// <summary>原始字节响应。</summary>
internal sealed class BytesResponse : IHttpResponse
{
    public HttpStatusCode Status { get; }
    public string? ContentType { get; }
    public byte[]? Body { get; }
    public IReadOnlyList<(string Key, string Value)> ExtraHeaders { get; } = Array.Empty<(string, string)>();
    public BytesResponse(string contentType, byte[] body) : this(contentType, body, HttpStatusCode.OK) { }
    public BytesResponse(string contentType, byte[] body, HttpStatusCode status)
    {
        ContentType = contentType;
        Body = body;
        Status = status;
    }
}
