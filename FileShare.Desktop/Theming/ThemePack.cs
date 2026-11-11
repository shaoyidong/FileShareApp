using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace FileShare.Desktop.Theming;

/// <summary>
/// 一份主题配色包（由「应用数据目录 / themes / *.json」加载）。
/// <para>
/// 格式：
/// <code>
/// {
///   "id": "win7", "name": "Windows 7（Aero）", "base": "win7",
///   "colors": {
///     "FsWindowBrush": "#F0F0F0",
///     "FsToolbarBrush": { "start": "#FDFEFF", "end": "#E6EEF9" },
///     "FsButtonFaceBrush": { "stops": [ { "offset": 0, "color": "#FCFCFD" }, ... ] },
///     "FsFontFamily": "Segoe UI, Microsoft YaHei"
///   }
/// }
/// </code>
/// <c>base</c> 决定使用哪一套编译期 axaml 基座（布局 Classes），<c>colors</c> 只覆盖调色板。
/// 未覆盖的键落回 axaml 里的默认值，因此主题包可以只写少数几个颜色。
/// </para>
/// </summary>
public sealed class ThemePack
{
    public const string DefaultId = "win7";
    public const string DefaultBase = "win7";

    /// <summary>可用的基座 id（对应 Themes/*.axaml 的编译期类）。</summary>
    public static readonly string[] ValidBases = { "win7", "winxp", "winxp-olive" };

    /// <summary>所有可调色板键。用于把基座默认值同步到 Application.Resources。</summary>
    public static IReadOnlyList<string> ResourceKeys { get; } = new[]
    {
        "FsWindowBrush", "FsSurfaceBrush", "FsCardBrush", "FsBorderBrush",
        "FsSoftBorderBrush", "FsTextBrush", "FsMutedBrush", "FsAccentBrush",
        "FsAccentTextBrush", "FsDangerBrush", "FsRowHoverBrush", "FsRowSelectedBrush",
        "FsButtonBorderBrush", "FsButtonBorderHoverBrush", "FsInputBorderBrush",
        "FsToolbarBrush", "FsButtonFaceBrush", "FsButtonHoverBrush",
        "FsButtonPressedBrush", "FsAccentButtonBrush", "FsAccentButtonHoverBrush",
        "FsFontFamily",
        "FsTooltipBrush", "FsTooltipTextBrush", "FsTooltipBorderBrush"
    };

    public ThemePack(string id, string name, string baseSkin, IReadOnlyDictionary<string, object?> colors)
    {
        Id = id;
        Name = name;
        Base = baseSkin;
        Colors = colors;
    }

    /// <summary>主题 id（文件名 / 持久化值）。</summary>
    public string Id { get; }

    /// <summary>设置页显示名。</summary>
    public string Name { get; }

    /// <summary>基座皮肤 id（win7 / winxp / winxp-olive）。</summary>
    public string Base { get; }

    /// <summary>调色板：键 → Avalonia 资源值（SolidColorBrush / LinearGradientBrush / FontFamily）。</summary>
    public IReadOnlyDictionary<string, object?> Colors { get; }

    /// <summary>解析一份主题 JSON；损坏 / 缺少必要字段时返回 false（AOT 安全，仅用 JsonDocument）。</summary>
    public static bool TryParse(string json, string? fallbackId, out ThemePack? pack)
    {
        pack = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // 语言包（code + translations）不是主题包，避免两个目录/嵌入资源互相污染
            if (root.TryGetProperty("translations", out _) ||
                root.TryGetProperty("code", out _) && !root.TryGetProperty("colors", out _))
            {
                return false;
            }

            var id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = fallbackId;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var name = ReadString(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = id;
            }

            var baseSkin = ReadString(root, "base");
            if (string.IsNullOrWhiteSpace(baseSkin) ||
                Array.IndexOf(ValidBases, baseSkin) < 0)
            {
                baseSkin = DefaultBase;
            }

            var colors = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (root.TryGetProperty("colors", out var colorsElement) &&
                colorsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in colorsElement.EnumerateObject())
                {
                    if (TryReadValue(property.Name, property.Value, out var value))
                    {
                        colors[property.Name] = value;
                    }
                }
            }

            // 没有 colors 段的空文件仍算合法主题（等于「基座原色」），便于用户先建文件再慢慢调色
            pack = new ThemePack(id!, name!, baseSkin!, colors);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadValue(string key, JsonElement element, out object? value)
    {
        value = null;

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (string.Equals(key, "FsFontFamily", StringComparison.OrdinalIgnoreCase))
            {
                value = new FontFamily(text);
                return true;
            }

            if (TryParseColor(text, out var color))
            {
                value = new SolidColorBrush(color);
                return true;
            }

            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // 字体也可以写成 { "family": "..." }
        if (TryReadFontFamily(element, out var font))
        {
            value = font;
            return true;
        }

        return TryReadGradient(element, out value);
    }

    private static bool TryReadFontFamily(JsonElement element, out FontFamily? font)
    {
        font = null;
        var family = ReadString(element, "family") ?? ReadString(element, "fontFamily");
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        font = new FontFamily(family);
        return true;
    }

    private static bool TryReadGradient(JsonElement element, out object? value)
    {
        value = null;
        var stops = new List<(double Offset, Color Color)>();

        if (element.TryGetProperty("stops", out var stopsElement) &&
            stopsElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            var count = stopsElement.GetArrayLength();
            foreach (var stop in stopsElement.EnumerateArray())
            {
                if (stop.ValueKind == JsonValueKind.String)
                {
                    if (!TryParseColor(stop.GetString(), out var color))
                    {
                        continue;
                    }

                    var offset = count <= 1 ? 0d : (double)index / (count - 1);
                    stops.Add((offset, color));
                    index++;
                }
                else if (stop.ValueKind == JsonValueKind.Object)
                {
                    var colorText = ReadString(stop, "color") ?? ReadString(stop, "value");
                    if (!TryParseColor(colorText, out var color))
                    {
                        continue;
                    }

                    double offset = 0;
                    if (stop.TryGetProperty("offset", out var offsetElement) &&
                        offsetElement.ValueKind == JsonValueKind.Number &&
                        offsetElement.TryGetDouble(out var parsed))
                    {
                        offset = parsed;
                    }

                    stops.Add((Math.Clamp(offset, 0, 1), color));
                }
            }
        }

        // 简写：{ "start": "#FFF", "end": "#000" }
        if (stops.Count == 0)
        {
            var start = ReadString(element, "start");
            var end = ReadString(element, "end");
            if (TryParseColor(start, out var startColor))
            {
                stops.Add((0, startColor));
            }

            if (TryParseColor(end, out var endColor))
            {
                stops.Add((1, endColor));
            }
        }

        if (stops.Count == 0)
        {
            return false;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };

        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        value = brush;
        return true;
    }

    private static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            color = Color.Parse(text.Trim());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
