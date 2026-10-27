using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileShare.Core.Web;

/// <summary>
/// 网页分享配置。
/// </summary>
public sealed class WebShareOptions
{
    /// <summary>监听端口。</summary>
    public int Port { get; init; } = 8088;

    /// <summary>访问 PIN（为空时不校验，不建议）。</summary>
    public string Pin { get; init; } = string.Empty;

    /// <summary>接收文件保存目录。</summary>
    public required string SaveDirectory { get; init; }

    /// <summary>对外展示的地址（可选；不填则自动选取本机 IPv4）。</summary>
    public string? PublicAddress { get; init; }
}

/// <summary>
/// 极简网页分享：在局域网内起一个 http 服务，其它设备用浏览器打开链接、输入 PIN 即可上传文件。
/// <para>不依赖管理员权限（不用 HttpListener 的 URL ACL），直接基于 TcpListener 实现最小 HTTP/1.1 子集。</para>
/// <para>只有两个端点：<c>GET /</c> 页面、<c>POST /upload?name=xxx</c> 上传（body 为原始文件字节，避免 multipart 解析）。</para>
/// </summary>
public interface IWebShareService : IDisposable
{
    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>浏览器访问地址（未运行时为 null）。</summary>
    string? Url { get; }

    /// <summary>当前 PIN。</summary>
    string Pin { get; }

    /// <summary>收到文件时触发（绝对路径）。</summary>
    event Action<string>? FileReceived;

    /// <summary>启动服务（重复调用会先停止旧实例）。</summary>
    void Start(WebShareOptions options);

    /// <summary>停止服务。</summary>
    void Stop();
}

/// <inheritdoc cref="IWebShareService"/>
public sealed class WebShareService : IWebShareService
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxConcurrentConnections = 8;

    private readonly ILogger<WebShareService> _logger;
    private readonly SemaphoreSlim _connectionGate = new(MaxConcurrentConnections, MaxConcurrentConnections);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _saveDirectory = string.Empty;
    private string _pin = string.Empty;
    private string? _url;

    public WebShareService(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<WebShareService>() ?? NullLogger<WebShareService>.Instance;
    }

    /// <inheritdoc />
    public bool IsRunning => _listener is not null;

    /// <inheritdoc />
    public string? Url => _url;

    /// <inheritdoc />
    public string Pin => _pin;

    /// <inheritdoc />
    public event Action<string>? FileReceived;

    /// <inheritdoc />
    public void Start(WebShareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Stop();

        if (string.IsNullOrWhiteSpace(options.SaveDirectory))
        {
            throw new ArgumentException("保存目录不能为空", nameof(options));
        }

        _saveDirectory = options.SaveDirectory;
        _pin = options.Pin ?? string.Empty;
        Directory.CreateDirectory(_saveDirectory);

        var port = options.Port is < 1 or > 65535 ? 8088 : options.Port;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();

        var address = string.IsNullOrWhiteSpace(options.PublicAddress)
            ? LocalAddress() ?? "127.0.0.1"
            : options.PublicAddress!;

        _url = $"http://{address}:{port}/";

        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));

        _logger.LogInformation("网页分享已启动: {Url}（保存目录 {Dir}）", _url, _saveDirectory);
    }

    /// <inheritdoc />
    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        _url = null;

        try
        {
            _cts?.Cancel();
        }
        catch (Exception)
        {
            // 忽略
        }

        try
        {
            listener?.Stop();
        }
        catch (Exception)
        {
            // 忽略
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _connectionGate.Dispose();
    }

    #region 连接处理

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "网页分享接受连接失败");
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                }

                continue;
            }

            await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "处理网页请求失败");
                }
                finally
                {
                    try { client.Close(); } catch (Exception) { }
                    _connectionGate.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        var (method, path, headers) = await ReadRequestHeadAsync(stream, cancellationToken).ConfigureAwait(false);

        if (method.Length == 0)
        {
            return;
        }

        var query = ParseQuery(path);

        headers.TryGetValue("content-length", out var contentLengthText);
        _ = long.TryParse(contentLengthText, out var declaredLength);

        // 客户端可能等待 100 Continue 才发送 body
        headers.TryGetValue("expect", out var expect);
        if (!string.IsNullOrEmpty(expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            var continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await stream.WriteAsync(continueBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith("/upload", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(stream, 405, "text/plain; charset=utf-8", "请使用 POST 上传", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/files", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(stream, 200, "application/json; charset=utf-8", BuildFileListJson(), cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(stream, 200, "text/html; charset=utf-8", WebPage, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !path.StartsWith("/upload", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextAsync(stream, 404, "text/plain; charset=utf-8", "Not Found", cancellationToken).ConfigureAwait(false);
            return;
        }

        // PIN 校验：支持 X-Pin 头或 ?pin= 查询串
        if (!string.IsNullOrEmpty(_pin))
        {
            headers.TryGetValue("x-pin", out var headerPin);
            query.TryGetValue("pin", out var queryPin);
            var provided = headerPin ?? queryPin;

            if (!string.Equals(provided, _pin, StringComparison.Ordinal))
            {
                // 先读完 body，避免客户端还在上传时收到响应导致连接被重置
                await DrainAsync(stream, declaredLength, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(stream, 401, "application/json; charset=utf-8",
                    "{\"ok\":false,\"error\":\"PIN 不正确\"}", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        query.TryGetValue("name", out var rawName);
        if (string.IsNullOrWhiteSpace(rawName))
        {
            await DrainAsync(stream, declaredLength, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(stream, 400, "application/json; charset=utf-8",
                "{\"ok\":false,\"error\":\"缺少文件名\"}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var fileName = Common.FileNameHelper.SanitizeFileName(rawName);
        var targetPath = Common.FileNameHelper.ResolveTargetPath(_saveDirectory, fileName, Models.FileNameConflictPolicy.Rename);
        var partPath = targetPath + ".part";

        var contentLength = declaredLength;
        if (contentLength < 0)
        {
            await WriteTextAsync(stream, 400, "application/json; charset=utf-8",
                "{\"ok\":false,\"error\":\"缺少 Content-Length\"}", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, System.IO.FileShare.None, 81920, useAsync: true))
            {
                await CopyExactAsync(stream, file, contentLength, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partPath, targetPath, overwrite: true);
            FileReceived?.Invoke(targetPath);
            _logger.LogInformation("网页分享收到文件: {Path} ({Size} 字节)", targetPath, contentLength);

            await WriteTextAsync(stream, 200, "application/json; charset=utf-8",
                "{\"ok\":true}", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "网页分享保存文件失败: {Path}", targetPath);
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch (Exception) { }

            await WriteTextAsync(stream, 500, "application/json; charset=utf-8",
                "{\"ok\":false,\"error\":\"保存失败\"}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 丢弃请求体（最多 64MB），用于「先拒绝、后响应」的错误路径。
    /// </summary>
    private static async Task DrainAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return;
        }

        var buffer = new byte[8192];
        var remaining = Math.Min(length, 64L * 1024 * 1024);

        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }
    private static async Task CopyExactAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("连接提前关闭，文件未传完");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    #endregion

    #region HTTP 解析

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers)> ReadRequestHeadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (string.Empty, string.Empty, headers);
            }

            length += read;

            // 以 "\r\n\r\n" 结束
            if (length >= 4 &&
                buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                break;
            }
        }

        var head = Encoding.UTF8.GetString(buffer, 0, length);
        var lines = head.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();

        var method = requestLine.Length > 0 ? requestLine[0] : string.Empty;
        var path = requestLine.Length > 1 ? requestLine[1] : "/";

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[key] = value;
        }

        return (method, path, headers);
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queryIndex = path.IndexOf('?');
        if (queryIndex < 0 || queryIndex == path.Length - 1)
        {
            return result;
        }

        foreach (var pair in path[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static async Task WriteTextAsync(Stream stream, int status, string contentType, string body, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Internal Server Error"
        };

        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {payload.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildFileListJson()
    {
        try
        {
            var files = Directory.EnumerateFiles(_saveDirectory)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(50)
                .Select(info => $"\"{info.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"")
                .ToArray();

            return "[" + string.Join(",", files) + "]";
        }
        catch (Exception)
        {
            return "[]";
        }
    }

    private static string? LocalAddress()
    {
        try
        {
            return Network.LocalNetworkHelper.GetLocalIPv4Addresses().FirstOrDefault()?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region 页面

    private const string WebPage = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>FileShare 网页传文件</title>
<style>
  body { font-family: "Segoe UI", Tahoma, "Microsoft YaHei", sans-serif; background:#ECE9D8; margin:0; padding:24px; }
  .card { max-width: 560px; margin: 0 auto; background:#fff; border:1px solid #7F9DB9; border-radius:3px; padding:20px; }
  h1 { font-size:18px; margin:0 0 12px; }
  p.hint { color:#555; font-size:13px; line-height:1.6; }
  .row { display:flex; gap:8px; align-items:center; margin:12px 0; }
  input[type=password] { padding:6px 8px; border:1px solid #7F9DB9; width:120px; }
  button { padding:7px 16px; border:1px solid #003C74; border-radius:3px; color:#fff; font-weight:bold;
           background:linear-gradient(#5B9BE8,#1B4C96); cursor:pointer; }
  button:disabled { opacity:.5; cursor:default; }
  #drop { border:2px dashed #9DB8D8; border-radius:4px; padding:28px; text-align:center; color:#456; margin:14px 0; }
  #drop.hover { background:#E3EEFC; }
  progress { width:100%; height:16px; }
  ul { padding-left:18px; color:#333; font-size:13px; }
  .err { color:#B00000; font-size:13px; }
</style>
</head>
<body>
<div class="card">
  <h1>FileShare · 网页传文件</h1>
  <p class="hint">在手机上打开这个页面，输入电脑上显示的 PIN，选择文件即可发送到电脑（无需安装 App）。</p>
  <div class="row">
    <label>PIN</label>
    <input id="pin" type="password" inputmode="numeric" placeholder="6 位数字" />
    <button id="sendBtn">选择文件并发送</button>
  </div>
  <input id="picker" type="file" multiple style="display:none" />
  <div id="drop">把文件拖到这里，或点上面的按钮选择文件</div>
  <progress id="bar" value="0" max="100" style="display:none"></progress>
  <div id="msg" class="err"></div>
  <ul id="list"></ul>
</div>
<script>
const picker = document.getElementById('picker');
const bar = document.getElementById('bar');
const msg = document.getElementById('msg');
const drop = document.getElementById('drop');
const pinBox = document.getElementById('pin');
const sendBtn = document.getElementById('sendBtn');
const list = document.getElementById('list');

pinBox.value = localStorage.getItem('fileshare_pin') || '';
pinBox.addEventListener('change', () => localStorage.setItem('fileshare_pin', pinBox.value));

async function upload(file) {
  const pin = pinBox.value.trim();
  if (!pin) { msg.textContent = '请先输入 PIN'; return; }
  msg.textContent = '';
  bar.style.display = 'block';
  bar.value = 0;

  const url = '/upload?name=' + encodeURIComponent(file.name) + '&pin=' + encodeURIComponent(pin);
  const xhr = new XMLHttpRequest();
  xhr.open('POST', url);
  xhr.setRequestHeader('X-Pin', pin);
  xhr.setRequestHeader('Content-Type', 'application/octet-stream');
  xhr.upload.onprogress = e => { if (e.lengthComputable) bar.value = e.loaded / e.total * 100; };

  const done = new Promise((resolve, reject) => {
    xhr.onload = () => {
      if (xhr.status === 200) { resolve(); } else {
        let text = xhr.responseText || ('HTTP ' + xhr.status);
        try { text = JSON.parse(text).error || text; } catch (e) {}
        reject(new Error(text));
      }
    };
    xhr.onerror = () => reject(new Error('网络错误'));
  });

  xhr.send(file);
  await done;
  const item = document.createElement('li');
  item.textContent = '✓ ' + file.name;
  list.prepend(item);
}

async function uploadAll(files) {
  for (const file of files) {
    try { await upload(file); }
    catch (e) { msg.textContent = file.name + '：' + e.message; }
  }
  bar.style.display = 'none';
}

sendBtn.addEventListener('click', () => picker.click());
picker.addEventListener('change', () => { if (picker.files.length) { uploadAll(picker.files); picker.value = ''; } });
drop.addEventListener('dragover', e => { e.preventDefault(); drop.classList.add('hover'); });
drop.addEventListener('dragleave', () => drop.classList.remove('hover'));
drop.addEventListener('drop', e => {
  e.preventDefault(); drop.classList.remove('hover');
  if (e.dataTransfer.files.length) uploadAll(e.dataTransfer.files);
});
</script>
</body>
</html>
""";

    #endregion
}
