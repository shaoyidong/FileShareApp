using FileShare.Desktop.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileShare.Desktop.Helpers;

/// <summary>桌面端 JSON 源生成上下文（AOT 友好）。</summary>
[JsonSerializable(typeof(DesktopUiPreferences))]
internal partial class DesktopJsonContext : JsonSerializerContext
{
}
