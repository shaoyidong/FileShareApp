using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileShare.Localsend.Http
{
    /// <summary>
    /// Minimal example HTTP receiver for LocalSend v2. For production, use ASP.NET Core and a proper multipart parser.
    /// This example demonstrates prepare-upload response (session + tokens) and accepting upload.
    /// </summary>
    public class LocalsendHttpReceiver : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly string _saveDir;
        private bool _running;

        public LocalsendHttpReceiver(string prefixUrl, string saveDirectory)
        {
            _listener = new HttpListener();
            if (!prefixUrl.EndsWith('/')) prefixUrl += "/";
            _listener.Prefixes.Add(prefixUrl);
            _saveDir = saveDirectory;
            Directory.CreateDirectory(_saveDir);
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            Task.Run(() => Loop());
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }

        private async Task Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;
                var path = req.Url?.AbsolutePath ?? string.Empty;
                if (req.HttpMethod == "POST" && path.EndsWith("/api/localsend/v2/prepare-upload"))
                {
                    using var sr = new StreamReader(req.InputStream);
                    var body = await sr.ReadToEndAsync().ConfigureAwait(false);
                    // parse files and return tokens
                    var session = Guid.NewGuid().ToString();
                    var files = new System.Collections.Generic.Dictionary<string, object>();
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in arr.EnumerateArray())
                            {
                                if (f.TryGetProperty("id", out var id))
                                {
                                    var t = Guid.NewGuid().ToString().Replace("-", "");
                                    files[id.GetString() ?? Guid.NewGuid().ToString()] = new { token = t };
                                }
                            }
                        }
                    }
                    catch { }

                    var result = new { session_id = session, files };
                    var bs = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
                    resp.ContentType = "application/json";
                    await resp.OutputStream.WriteAsync(bs, 0, bs.Length).ConfigureAwait(false);
                    resp.Close();
                    return;
                }
                else if (req.HttpMethod == "POST" && path.EndsWith("/api/localsend/v2/upload"))
                {
                    // naive parse: save entire body as file with timestamp
                    var filename = "upload_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var outPath = Path.Combine(_saveDir, filename);
                    using var fs = File.Create(outPath);
                    await req.InputStream.CopyToAsync(fs).ConfigureAwait(false);
                    fs.Flush();
                    // in real implementation parse multipart and extract actual file data
                    resp.StatusCode = 200;
                    var ok = Encoding.UTF8.GetBytes("ok");
                    await resp.OutputStream.WriteAsync(ok, 0, ok.Length).ConfigureAwait(false);
                    resp.Close();
                    return;
                }
                else
                {
                    resp.StatusCode = 404; resp.Close(); return;
                }
            }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }

        public void Dispose()
        {
            Stop();
            _listener.Close();
        }
    }
}
