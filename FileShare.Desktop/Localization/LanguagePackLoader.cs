using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FileShare.Desktop.Localization;

/// <summary>
/// 语言包文件加载器（AOT 安全：只用 <see cref="JsonDocument"/> 与文件 IO，不依赖反射序列化）。
/// <para>
/// 语言包存放于「应用数据目录 / i18n / *.json」，格式：
/// <code>
/// { "code": "en-US", "displayName": "English", "translations": { "中文原文": "English text" } }
/// </code>
/// 首次启动会把随程序发布的内置语言包（输出目录 Localization/packs/*.json）复制到数据目录；
/// 之后一律以数据目录的文件为准，且不会覆盖用户已修改过的文件。
/// 因此应用发布后可以直接改文件改翻译，或按同一格式放入新文件来新增一套语言。
/// </para>
/// </summary>
public static class LanguagePackLoader
{
    public const string PackFolderName = "i18n";

    private static readonly string TemplateDirectory =
        Path.Combine(AppContext.BaseDirectory, "Localization", "packs");

    private static string _packsDirectory = string.Empty;

    /// <summary>当前语言包目录（数据目录 / i18n）。</summary>
    public static string PacksDirectory => _packsDirectory;

    /// <summary>初始化：准备目录 → 复制内置包（不覆盖）→ 加载全部语言包。</summary>
    public static void Initialize(string dataDirectory)
    {
        _packsDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(dataDirectory) ? AppContext.BaseDirectory : dataDirectory,
            PackFolderName);

        Directory.CreateDirectory(_packsDirectory);
        SeedBuiltInPacks();
        Reload();
    }

    /// <summary>把随程序发布的内置语言包复制到数据目录（已存在的用户文件不覆盖）。</summary>
    private static void SeedBuiltInPacks()
    {
        // 优先用嵌入资源：测试项目引用、单文件发布等场景下输出目录不一定有模板文件
        try
        {
            var assembly = typeof(LanguagePackLoader).Assembly;
            // 必须用完整路径：".packs." 会同时命中 Themes/packs/*.json（主题配色包），
            // 导致主题 id 被注册成「语言」出现在界面语言下拉框里
            const string marker = ".Localization.packs.";
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var index = resource.LastIndexOf(marker, StringComparison.Ordinal);
                if (index < 0 || !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = resource[(index + marker.Length)..];
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
                    // 版本升级后内置包新增了译文 key：只补齐缺失项，不覆盖用户改过的译文
                    using var reader = new StreamReader(stream);
                    SeedInto(target, reader.ReadToEnd());
                }
            }
        }
        catch (Exception)
        {
            // 忽略，继续尝试输出目录
        }

        // 兼容直接从输出目录 Localization/packs 复制（普通发布形态）
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
            // 复制失败时仍允许直接读取数据目录里已有的语言包
        }
    }

    /// <summary>
    /// 把内置包里「本地文件缺少」的译文合并进去（保留用户已有译文与自定义字段）。
    /// 这样发布新版本后新增的界面文案也能自动获得翻译，不必要求用户删掉旧语言包。
    /// <para>AOT 安全：只用 JsonDocument + Utf8JsonWriter，不用反射序列化。</para>
    /// </summary>
    private static void SeedInto(string target, string builtInJson)
    {
        try
        {
            using var builtIn = JsonDocument.Parse(builtInJson);
            using var local = JsonDocument.Parse(File.ReadAllText(target));
            if (!builtIn.RootElement.TryGetProperty("translations", out var builtInTranslations) ||
                builtInTranslations.ValueKind != JsonValueKind.Object ||
                !local.RootElement.TryGetProperty("translations", out var localTranslations) ||
                localTranslations.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var merged = new Dictionary<string, string>();
            foreach (var property in localTranslations.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    merged[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            var changed = false;
            foreach (var property in builtInTranslations.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && !merged.ContainsKey(property.Name))
                {
                    merged[property.Name] = property.Value.GetString() ?? string.Empty;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var property in local.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("translations"))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WritePropertyName("translations");
                writer.WriteStartObject();
                foreach (var (key, value) in merged)
                {
                    writer.WriteString(key, value);
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

    /// <summary>重新扫描语言包文件并注册；返回成功加载的文件数。</summary>
    public static int Reload()
    {
        if (string.IsNullOrEmpty(_packsDirectory) || !Directory.Exists(_packsDirectory))
        {
            return 0;
        }

        var loaded = 0;
        foreach (var file in Directory.EnumerateFiles(_packsDirectory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 严格格式校验：必须是带 translations 字典的语言包。
                // 主题配色包（colors / base）以及数据目录里残留的其它 JSON 不该混进语言列表。
                if (root.TryGetProperty("colors", out _) || root.TryGetProperty("base", out _) ||
                    !root.TryGetProperty("translations", out var translations) ||
                    translations.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var code = ReadString(root, "code") ?? Path.GetFileNameWithoutExtension(file);
                var displayName = ReadString(root, "displayName") ?? code;
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                var map = new Dictionary<string, string>();
                foreach (var property in translations.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        map[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }

                Loc.Register(new JsonLanguagePack(code, displayName, map));
                loaded++;
            }
            catch (Exception)
            {
                // 单个损坏 / 格式不符的文件不影响其它语言包
            }
        }

        return loaded;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
