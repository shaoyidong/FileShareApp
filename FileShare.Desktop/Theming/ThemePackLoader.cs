using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using FileShare.Core.Services;

namespace FileShare.Desktop.Theming;

/// <summary>
/// 主题配色包加载器（镜像 <see cref="FileShare.Desktop.Localization.LanguagePackLoader"/> 的 i18n 模式）。
/// <para>
/// 主题存放于「应用数据目录 / themes / *.json」，内置主题以嵌入资源随程序发布，
/// 首次启动复制到数据目录且不覆盖用户已修改过的文件。发布后直接改 JSON 改颜色，
/// 或按同样格式放入新文件即可新增主题，设置页点「重载主题」立即生效。
/// </para>
/// <para>AOT 安全：只用 <see cref="System.Text.Json.JsonDocument"/> 与文件 IO，不依赖反射序列化。</para>
/// </summary>
public static class ThemePackLoader
{
    public const string PackFolderName = "themes";

    /// <summary>嵌入资源名中的路径标记（Themes/packs/*.json → *.Themes.packs.*.json）。</summary>
    private const string EmbeddedMarker = ".Themes.packs.";

    private static readonly string TemplateDirectory =
        Path.Combine(AppContext.BaseDirectory, "Themes", "packs");

    private static readonly Lock _gate = new();
    private static readonly List<ThemePack> _packs = new();
    private static readonly Dictionary<string, ThemePack> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    private static string _packsDirectory = string.Empty;

    /// <summary>当前主题包目录（数据目录 / themes）。</summary>
    public static string PacksDirectory => _packsDirectory;

    /// <summary>初始化：准备目录 → 复制内置主题（不覆盖）→ 扫描注册全部主题。</summary>
    public static void Initialize(string dataDirectory)
    {
        _packsDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(dataDirectory) ? AppContext.BaseDirectory : dataDirectory,
            PackFolderName);

        Directory.CreateDirectory(_packsDirectory);
        SeedBuiltInPacks();
        Reload();
    }

    /// <summary>重新扫描 themes 目录并注册；返回成功加载的文件数。</summary>
    public static int Reload()
    {
        EnsureInitialized();
        if (!Directory.Exists(_packsDirectory))
        {
            return 0;
        }

        var loaded = 0;
        foreach (var file in Directory.EnumerateFiles(_packsDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                if (!ThemePack.TryParse(json, Path.GetFileNameWithoutExtension(file), out var pack) ||
                    pack is null)
                {
                    continue;
                }

                Register(pack);
                loaded++;
            }
            catch (Exception)
            {
                // 单个损坏 / 格式不符的文件不影响其它主题
            }
        }

        return loaded;
    }

    /// <summary>已注册的主题（按注册顺序，用于生成设置页下拉框）。</summary>
    public static IReadOnlyList<ThemePack> Packs
    {
        get
        {
            EnsureInitialized();
            lock (_gate)
            {
                return _packs.ToArray();
            }
        }
    }

    /// <summary>按 id 查找主题；未注册返回 null。</summary>
    public static ThemePack? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        EnsureInitialized();
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var pack) ? pack : null;
        }
    }

    private static void EnsureInitialized()
    {
        if (!string.IsNullOrEmpty(_packsDirectory))
        {
            return;
        }

        Initialize(AppPaths.GetDataDirectory());
    }

    private static void Register(ThemePack pack)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(pack.Id, out var existing))
            {
                var index = _packs.IndexOf(existing);
                _packs[index] = pack;
                _byId[pack.Id] = pack;
            }
            else
            {
                _packs.Add(pack);
                _byId.Add(pack.Id, pack);
            }
        }
    }

    /// <summary>把随程序发布的内置主题复制到数据目录（已存在的用户文件不覆盖）。</summary>
    private static void SeedBuiltInPacks()
    {
        // 优先用嵌入资源：测试项目引用、单文件发布等场景下输出目录不一定有模板文件
        try
        {
            var assembly = typeof(ThemePackLoader).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var index = resource.LastIndexOf(EmbeddedMarker, StringComparison.Ordinal);
                if (index < 0 || !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = resource[(index + EmbeddedMarker.Length)..];
                var target = Path.Combine(_packsDirectory, fileName);

                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                if (!File.Exists(target))
                {
                    using var output = File.Create(target);
                    stream.CopyTo(output);
                }
                else
                {
                    // 版本升级后内置主题新增配色键：只补齐缺失项，保留用户调过的颜色
                    using var reader = new StreamReader(stream);
                    SeedInto(target, reader.ReadToEnd());
                }
            }
        }
        catch (Exception)
        {
            // 忽略，继续尝试输出目录
        }

        // 兼容直接从输出目录 Themes/packs 复制（普通发布形态）
        try
        {
            if (!Directory.Exists(TemplateDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(TemplateDirectory, "*.json"))
            {
                var target = Path.Combine(_packsDirectory, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                }
                else
                {
                    SeedInto(target, File.ReadAllText(file));
                }
            }
        }
        catch (Exception)
        {
            // 复制失败时仍允许直接读取数据目录里已有的主题文件
        }
    }

    /// <summary>
    /// 把内置主题里「本地文件缺少」的配色键合并进去（保留用户已有颜色与自定义字段）。
    /// AOT 安全：只用 JsonDocument + Utf8JsonWriter。
    /// </summary>
    private static void SeedInto(string target, string builtInJson)
    {
        try
        {
            if (!ThemePack.TryParse(builtInJson, Path.GetFileNameWithoutExtension(target), out _) ||
                !ThemePack.TryParse(File.ReadAllText(target), Path.GetFileNameWithoutExtension(target), out _))
            {
                return;
            }

            using var builtIn = JsonDocument.Parse(builtInJson);
            using var local = JsonDocument.Parse(File.ReadAllText(target));
            if (!builtIn.RootElement.TryGetProperty("colors", out var builtInColors) ||
                builtInColors.ValueKind != JsonValueKind.Object ||
                !local.RootElement.TryGetProperty("colors", out var localColors) ||
                localColors.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in localColors.EnumerateObject())
            {
                existing.Add(property.Name);
            }

            var missing = builtInColors.EnumerateObject()
                .Where(property => !existing.Contains(property.Name))
                .ToArray();
            if (missing.Length == 0)
            {
                return;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var property in local.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("colors"))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WritePropertyName("colors");
                writer.WriteStartObject();
                foreach (var property in localColors.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
                foreach (var property in missing)
                {
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            File.WriteAllText(target, Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception)
        {
            // 本地文件损坏 / 不可写时保持原样，Reload 会自行跳过
        }
    }
}
