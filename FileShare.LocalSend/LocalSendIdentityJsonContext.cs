using System.Text.Json.Serialization;

namespace FileShare.LocalSend;

/// <summary>
/// 适配层本地持久化（身份文件 / TOFU 信任库）的 JSON 源生成上下文。
/// <para>保持原有 PascalCase 字段名与缩进格式，兼容已经存在的 localsend-*.json。</para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdentityModel))]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "StringMap")]
internal partial class LocalSendIdentityJsonContext : JsonSerializerContext
{
}
