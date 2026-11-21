using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FileShare.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services;

/// <summary>
/// 关于窗口「可变内容」的一条配置。字段全部可空：远程/本地配置缺项时由
/// <see cref="AboutContentService.Defaults"/> 兜底，界面不会因为配置写错而空白。
/// </summary>
public sealed class AboutContent
{
    /// <summary>false = 整块「动态信息」隐藏（找到工作后可直接关掉，不用重新发版）。</summary>
    public bool Enabled { get; init; } = true;

    public string? Developer { get; init; }
    public string? Copyright { get; init; }

    /// <summary>主标题，例如求职意向。</summary>
    public string? Headline { get; init; }

    /// <summary>副标题，例如「欢迎扫码联系」。</summary>
    public string? Subtitle { get; init; }

    /// <summary>
    /// 二维码/图片来源：<c>"builtin"</c>（内置微信二维码）、<c>"none"</c>（不显示）、
    /// 数据目录下的相对路径、绝对路径，或 http(s) 图片地址。
    /// </summary>
    public string? QrImage { get; init; }

    public string? LinkText { get; init; }
    public string? LinkUrl { get; init; }

    /// <summary>远程配置文件地址。只读本地文件里的这一项，远程配置无法篡改内容源。</summary>
    public string? SourceUrl { get; init; }
}

/// <summary>
/// 关于窗口内容服务：让「已发布的版本」也能换掉关于界面里的求职信息。
/// <para>
/// 三级数据源，优先级由低到高：
/// <list type="number">
///   <item>编译进程序的内置默认（就是现在的求职信息 + 内置二维码），保证离线有内容；</item>
///   <item>数据目录 <c>about.json</c>（首次启动自动生成，可直接编辑，发布后改本机立即生效）；</item>
///   <item>固定 URL 上的远程 <c>about.json</c>（后台拉取并写回本地缓存）——这才是「别人电脑上
///         已安装的版本」也能跟着更新的关键：<b>URL 编译期固定，内容随时可改</b>。</item>
/// </list>
/// 网络失败 / 超时 / JSON 损坏一律静默回退到上一次成功的内容，不影响主程序。
/// </para>
/// <para>AOT 安全：只用 <see cref="JsonDocument"/> + <see cref="Utf8JsonWriter"/> 手工解析，不用反射序列化。</para>
/// </summary>
public sealed class AboutContentService : IAboutContentService
{
    /// <summary>
    /// 发布前把这里填成一个你能随时修改的地址；留空则退化为「纯本地 about.json」模式
    /// （只能改自己这台机器）。以下三种免费托管格式都是「URL 固定、内容随时改」：
    /// <para>・Gitee 公开仓库：<c>https://gitee.com/{用户}/{仓库}/raw/master/about.json</c>
    /// （会 302 到 raw.giteeusercontent.com，HttpClient 自动跟随；服务端缓存 1~5 分钟）</para>
    /// <para>・GitHub Gist：<c>https://gist.githubusercontent.com/{用户}/{gistId}/raw/about.json</c>
    /// （必须删掉 Raw 链接里的 commit hash，否则永远指向旧版本）</para>
    /// <para>・对象存储 / 自有域名：<c>https://你的域名/about.json</c>（公共读，改完即时生效）</para>
    /// 也可以不填这里，而改数据目录 about.json 的 <c>sourceUrl</c>（只影响本机）。
    /// </summary>
    public const string DefaultSourceUrl =
        "https://gitee.com/shaoyidong/fileshare-about/raw/master/about.json";

    private const string FileName = "about.json";
    private const string QrCacheFileName = "about-qr.cache";
    private const int MaxConfigBytes = 64 * 1024;
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private const int MaxTextLength = 160;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly AboutContent EmptyContent = new();
    // 注意：静态字段按声明顺序初始化，内置默认必须在 EmptyContent 之后解析
    private static readonly AboutContent BuiltinDefaults = Parse(BuiltInJson);
    private static readonly Lazy<IImage?> BuiltinQr = new(LoadBuiltinQr);

    private readonly Lock _gate = new();
    private readonly ILogger<AboutContentService> _logger;
    private readonly SynchronizationContext? _uiContext;
    private int _refreshing;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    private string _dataDirectory = AppPaths.GetDataDirectory();
    private string _filePath;
    private string _qrCachePath;
    private AboutContent _current = BuiltinDefaults;
    private IImage? _qrImage;    

    public AboutContentService(SynchronizationContext uiContext,ILogger<AboutContentService>? logger = null)
    {
        _logger = logger ?? NullLogger<AboutContentService>.Instance;
        _uiContext = uiContext ?? SynchronizationContext.Current;
        _filePath = Path.Combine(_dataDirectory, FileName);
        _qrCachePath = Path.Combine(_dataDirectory, QrCacheFileName);
    }

    /// <summary>内容发生变化（远程刷新成功后）在 UI 线程触发，视图据此重新绑定。</summary>
    public event Action? ContentChanged;

    /// <summary>内置默认配置（与首次生成的 about.json 一致）。</summary>
    public static AboutContent Defaults => BuiltinDefaults;

    /// <summary>当前生效的内容。</summary>
    public AboutContent Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>当前生效的二维码/图片；null 表示不显示。</summary>
    public IImage? QrImage
    {
        get { lock (_gate) { return _qrImage; } }
    }

    /// <summary>本地配置文件路径（可告知用户直接编辑）。</summary>
    public string LocalFilePath
    {
        get { lock (_gate) { return _filePath; } }
    }

    /// <summary>
    /// 初始化：准备数据目录 → 首次生成 about.json → 立即应用本地内容。
    /// 应在任何窗口创建之前调用（见 App.axaml.cs）。
    /// </summary>
    public void Initialize(string? dataDirectory = null)
    {   
        lock (_gate)
        {
            _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
                ? AppPaths.GetDataDirectory()
                : dataDirectory!;
            _filePath = Path.Combine(_dataDirectory, FileName);
            _qrCachePath = Path.Combine(_dataDirectory, QrCacheFileName);
        }

        EnsureLocalFile();
        Apply(ReadLocalContent());
    }

    /// <summary>
    /// 后台拉取远程配置。<paramref name="force"/> = true 时忽略刷新间隔（打开关于窗口时用）。
    /// 全程不抛异常：任何失败都保留原有内容。
    /// </summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!force && DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
            {
                return;
            }

            _lastRefreshUtc = DateTime.UtcNow;

            string localPath;
            string cachePath;
            lock (_gate)
            {
                localPath = _filePath;
                cachePath = _qrCachePath;
            }

            var local = ReadLocalContent();
            var url = FirstNonEmpty(local.SourceUrl, DefaultSourceUrl);
            if (!IsHttpUrl(url))
            {
                return; // 纯本地模式
            }

            using var cts = new CancellationTokenSource(RequestTimeout);
            using var client = new HttpClient();
            // 绕开 CDN / 代理缓存，保证改完远程文件能尽快生效
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");

            var bytes = await DownloadLimitedAsync(client, new Uri(url!), MaxConfigBytes, cts.Token)
                .ConfigureAwait(false);
            if (bytes is null || !LooksLikeAboutConfig(bytes))
            {
                return;
            }

            // 内容源只由本地掌握：远程配置里的 sourceUrl 一律丢弃，避免被劫持后改指向别处
            var merged = MergeSourceUrl(Encoding.UTF8.GetString(bytes), local.SourceUrl);
            AtomicWrite(localPath, Encoding.UTF8.GetBytes(merged));

            var parsed = Parse(merged);
            await TryCacheRemoteImageAsync(client, parsed.QrImage, cachePath, cts.Token)
                .ConfigureAwait(false);

            Apply(WithDefaults(parsed));
        }
        catch
        {
            // 断网 / DNS 失败 / 超时 / 磁盘不可写：静默沿用旧内容
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // ---------------------------------------------------------------- 解析

    private static AboutContent Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return EmptyContent;
            }

            return new AboutContent
            {
                Enabled = ReadBool(root, "enabled") ?? true,
                Developer = ReadText(root, "developer"),
                Copyright = ReadText(root, "copyright"),
                Headline = ReadText(root, "headline"),
                Subtitle = ReadText(root, "subtitle"),
                QrImage = ReadString(root, "qrImage", 512),
                LinkText = ReadText(root, "linkText"),
                LinkUrl = ReadString(root, "linkUrl", 512),
                SourceUrl = ReadString(root, "sourceUrl", 512)
            };
        }
        catch
        {
            return new AboutContent();
        }
    }

    /// <summary>
    /// 读文案字段：纯字符串，不做 i18n（关于窗口内容本就是发布者随时改的一段话，
    /// 再套一层语言字典是冗余）。区分「字段缺失」(null → 用内置默认补齐) 与
    /// 「显式写成空串」("" → 这一项不显示)。
    /// </summary>
    private static string? ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { } value)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }

    private static string? ReadString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    }

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    // ---------------------------------------------------------------- 本地文件

    private void EnsureLocalFile()
    {
        string path;
        lock (_gate)
        {
            path = _filePath;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(path))
            {
                AtomicWrite(path, Encoding.UTF8.GetBytes(BuiltInJson));
            }
        }
        catch
        {
            // 目录只读时仍可用内置默认内容
        }
    }

    private AboutContent ReadLocalContent()
    {
        string path;
        lock (_gate)
        {
            path = _filePath;
        }

        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxConfigBytes)
            {
                return Defaults;
            }

            var content = Parse(File.ReadAllText(path));
            return WithDefaults(content);
        }
        catch
        {
            return BuiltinDefaults;
        }
    }

    /// <summary>单字段缺失时用内置默认补齐，避免用户只改一行导致其它信息消失。</summary>
    private static AboutContent WithDefaults(AboutContent content) => new()
    {
        Enabled = content.Enabled,
        Developer = content.Developer ?? BuiltinDefaults.Developer,
        Copyright = content.Copyright ?? BuiltinDefaults.Copyright,
        Headline = content.Headline ?? BuiltinDefaults.Headline,
        Subtitle = content.Subtitle ?? BuiltinDefaults.Subtitle,
        QrImage = content.QrImage ?? BuiltinDefaults.QrImage,
        LinkText = content.LinkText ?? BuiltinDefaults.LinkText,
        LinkUrl = content.LinkUrl ?? BuiltinDefaults.LinkUrl,
        SourceUrl = content.SourceUrl
    };

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>远程配置里剔除 sourceUrl，写回本地自身的值（AOT 安全）。</summary>
    private static string MergeSourceUrl(string json, string? sourceUrl)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            // 保留中文原文，方便用户直接打开 about.json 编辑（默认会把中文转成 \uXXXX）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("sourceUrl"))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteString("sourceUrl", sourceUrl ?? string.Empty);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool LooksLikeAboutConfig(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, JsonOptions);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   (root.TryGetProperty("schema", out _) ||
                    root.TryGetProperty("enabled", out _) ||
                    root.TryGetProperty("headline", out _));
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 图片

    private static Bitmap? LoadBuiltinQr()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://FileShare.Desktop/Assets/wechat-qr.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private IImage? ResolveQr(string? spec)
    {
        if (string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(spec) ||
            string.Equals(spec, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            return BuiltinQr.Value;
        }

        if (IsHttpUrl(spec))
        {
            string cache;
            lock (_gate)
            {
                cache = _qrCachePath;
            }

            return TryLoadImageFile(cache) ?? BuiltinQr.Value;
        }

        string path;
        lock (_gate)
        {
            path = Path.IsPathRooted(spec)
                ? spec
                : Path.Combine(_dataDirectory, spec);
        }

        return TryLoadImageFile(path) ?? BuiltinQr.Value;
    }

    private static Bitmap? TryLoadImageFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var length = new FileInfo(path).Length;
            if (length is <= 0 or > MaxImageBytes)
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task TryCacheRemoteImageAsync(
        HttpClient client, string? spec, string cachePath, CancellationToken ct)
    {
        if (!IsHttpUrl(spec))
        {
            return;
        }

        try
        {
            var bytes = await DownloadLimitedAsync(client, new Uri(spec!), MaxImageBytes, ct)
                .ConfigureAwait(false);
            if (bytes is null || !LooksLikeImage(bytes))
            {
                return;
            }

            AtomicWrite(cachePath, bytes);
        }
        catch
        {
            // 图片下载失败时沿用旧缓存 / 内置二维码
        }
    }

    private static bool LooksLikeImage(byte[] bytes) => bytes.Length >= 12 &&
        (IsPng(bytes) || IsJpeg(bytes) || IsGif(bytes) || IsWebP(bytes));

    private static bool IsPng(byte[] b) =>
        b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static bool IsJpeg(byte[] b) => b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsGif(byte[] b) =>
        b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F';

    private static bool IsWebP(byte[] b) =>
        b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' &&
        b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    // ---------------------------------------------------------------- 工具

    private void Apply(AboutContent content)
    {
        lock (_gate)
        {
            _current = content;
            _qrImage = ResolveQr(content.QrImage);
        }

        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var handler = ContentChanged;
        if (handler is null)
        {
            return;
        }

        var context = _uiContext;
        if (context is null || SynchronizationContext.Current == context)
        {
            InvokeSafely(handler);
        }
        else
        {
            context.Post(_ => InvokeSafely(handler), null);
        }
    }

    private void InvokeSafely(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关于窗口内容更新通知失败");
        }
    }

    private static async Task<byte[]?> DownloadLimitedAsync(
        HttpClient client, Uri uri, int maxBytes, CancellationToken ct)
    {
        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsHttpUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first!.Trim()
        : !string.IsNullOrWhiteSpace(second) ? second!.Trim()
        : null;

    /// <summary>首次启动写入数据目录的模板，也是编译期内置默认内容。</summary>
    private const string BuiltInJson = """
        {          
          "enabled": true,
          "sourceUrl": "",
          "developer": "Developed by yidong.shao",
          "copyright": "Copyright © 2026",
          "headline": "求职长沙：应用软件 / 上位机软件开发",
          "subtitle": "欢迎用人单位扫码联系",
          "qrImage": "builtin",
          "linkText": "",
          "linkUrl": ""
        }
        """;
}
