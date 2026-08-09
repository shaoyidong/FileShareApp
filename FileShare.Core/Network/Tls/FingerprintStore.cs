using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace FileShare.Core.Network.Tls;

/// <summary>
/// 证书指纹 TOFU（Trust On First Use）信任库。
/// <para>策略：首次连接某设备时，记录其证书 SHA256 指纹及过期时间；后续连接若指纹不一致，
/// 但旧证书已过期，则视为合法续期，自动更新指纹；否则判定为中间人攻击，拒绝连接。</para>
/// <para>存储格式：纯文本行（deviceId:SHA256指纹:NotAfterUtcTicks），向后兼容旧格式（无过期时间）。</para>
/// </summary>
public sealed class FingerprintStore
{
    private readonly string _storePath;
    private readonly ILogger<FingerprintStore>? _logger;
    private readonly object _lock = new();

    public FingerprintStore(string storePath, ILogger<FingerprintStore>? logger = null)
    {
        _storePath = storePath;
        _logger = logger;
    }

    /// <summary>
    /// 计算证书的 SHA256 指纹（证书 DER 编码的哈希，十六进制小写）。
    /// </summary>
    public static string ComputeFingerprint(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(certificate.RawData);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 校验并按 TOFU 策略记录指纹（基于证书对象）。
    /// <para>首次见到该设备证书 → 记录（指纹 + 过期时间）并返回 true。</para>
    /// <para>指纹一致 → 更新过期时间（若新过期时间更晚）并返回 true。</para>
    /// <para>指纹不一致但旧证书已过期 → 自动续期，更新记录并返回 true。</para>
    /// <para>指纹不一致且旧证书未过期 → 返回 false（疑似 MITM）。</para>
    /// </summary>
    public bool ValidateAndStore(string deviceId, X509Certificate2 certificate)
    {
        string fingerprint = ComputeFingerprint(certificate);
        DateTime notAfterUtc = certificate.NotAfter.ToUniversalTime();

        lock (_lock)
        {
            var entries = LoadEntries();
            if (entries.TryGetValue(deviceId, out var known))
            {
                // 指纹匹配：更新过期时间（若新时间更晚）
                if (string.Equals(known.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    if (notAfterUtc > known.NotAfterUtc)
                    {
                        entries[deviceId] = new Entry(fingerprint, notAfterUtc);
                        SaveEntries(entries);
                        _logger?.LogDebug("设备 {DeviceId} 证书指纹不变，更新过期时间至 {NotAfter}", deviceId, notAfterUtc);
                    }
                    return true;
                }

                // 指纹不匹配：检查旧证书是否已过期（且旧记录包含有效过期时间）
                if (known.NotAfterUtc != DateTime.MinValue && known.NotAfterUtc < DateTime.UtcNow)
                {
                    // 合法续期：旧证书已过期，更新为新指纹和新过期时间
                    entries[deviceId] = new Entry(fingerprint, notAfterUtc);
                    SaveEntries(entries);
                    _logger?.LogInformation("设备 {DeviceId} 的证书已自动续期（旧证书于 {OldNotAfter} 过期）", deviceId, known.NotAfterUtc);
                    return true;
                }

                // 指纹不匹配且旧证书未过期或未知 -> 拒绝
                _logger?.LogWarning("设备 {DeviceId} 的证书指纹不匹配且旧证书未过期：已知={Known}，实际={Actual}，疑似中间人攻击", deviceId, known.Fingerprint, fingerprint);
                return false;
            }

            // 首次信任：记录指纹和过期时间
            entries[deviceId] = new Entry(fingerprint, notAfterUtc);
            SaveEntries(entries);
            _logger?.LogInformation("首次记录设备 {DeviceId} 的证书指纹，有效期至 {NotAfter}", deviceId, notAfterUtc);
            return true;
        }
    }

    // 内部条目结构
    private record Entry(string Fingerprint, DateTime NotAfterUtc);

    private Dictionary<string, Entry> LoadEntries()
    {
        var dict = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_storePath)) return dict;
            foreach (var line in File.ReadAllLines(_storePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(':');
                if (parts.Length < 2) continue;
                var id = parts[0].Trim();
                var fp = parts[1].Trim();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(fp)) continue;

                DateTime notAfter = DateTime.MinValue;
                if (parts.Length >= 3 && long.TryParse(parts[2], out var ticks))
                {
                    try { notAfter = new DateTime(ticks, DateTimeKind.Utc); }
                    catch { /* 忽略无效刻度 */ }
                }
                dict[id] = new Entry(fp, notAfter);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "加载指纹信任库失败");
        }
        return dict;
    }

    private void SaveEntries(Dictionary<string, Entry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = entries.Select(kv => $"{kv.Key}:{kv.Value.Fingerprint}:{kv.Value.NotAfterUtc.Ticks}");
            File.WriteAllLines(_storePath, lines);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "保存指纹信任库失败");
        }
    }
}