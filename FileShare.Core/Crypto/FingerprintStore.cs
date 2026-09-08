using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace FileShare.Core.Crypto;

/// <summary>
/// 证书指纹 TOFU（Trust On First Use）信任库。
/// <para>策略：首次连接某设备时，记录其证书 SHA256 指纹及过期时间；后续连接若指纹不一致，
/// 但旧证书已过期，则视为合法续期，自动更新指纹；否则判定为中间人攻击，拒绝连接。</para>
/// <para>存储格式：纯文本行（deviceId:SHA256指纹:NotAfterUtcTicks），向后兼容旧格式（无过期时间）。</para>
/// </summary>
public sealed class FingerprintStore
{
    private readonly string _storePath;
    private readonly object _lock = new();

    public FingerprintStore(string storePath)
    {
        _storePath = storePath;
    }

    /// <summary>
    /// 计算证书的 SHA256 指纹（证书 DER 编码的哈希，十六进制小写）。
    /// </summary>
    private static string ComputeFingerprint(X509Certificate2 certificate)
    {
        return CertVerifier.FingerprintFromCertDer(certificate.RawData).ToLowerInvariant();
    }

    /// <summary>
    /// 校验并按 TOFU 策略记录指纹（基于证书对象）。
    /// <para>首次见到该设备证书 → 记录（指纹 + 过期时间）并返回 true。</para>
    /// <para>指纹一致 → 更新过期时间（若新过期时间更晚）并返回 true。</para>
    /// <para>指纹不一致但旧证书已过期 → 自动续期，更新记录并返回 true。</para>
    /// <para>指纹不一致且旧证书未过期 → 返回 false（疑似 MITM）。</para>
    /// </summary>
    /// <exception cref="IOException">加载或保存存储文件时发生 I/O 错误。</exception>
    /// <exception cref="FormatException">存储文件格式损坏，无法解析。</exception>
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
                    }
                    return true;
                }

                // 指纹不匹配：检查旧证书是否已过期（且旧记录包含有效过期时间）
                if (known.NotAfterUtc != DateTime.MinValue && known.NotAfterUtc < DateTime.UtcNow)
                {
                    // 合法续期：旧证书已过期，更新为新指纹和新过期时间
                    entries[deviceId] = new Entry(fingerprint, notAfterUtc);
                    SaveEntries(entries);
                    return true;
                }

                // 指纹不匹配且旧证书未过期或未知 -> 拒绝
                return false;
            }

            // 首次信任：记录指纹和过期时间
            entries[deviceId] = new Entry(fingerprint, notAfterUtc);
            SaveEntries(entries);
            return true;
        }
    }

    private record Entry(string Fingerprint, DateTime NotAfterUtc);

    private Dictionary<string, Entry> LoadEntries()
    {
        var dict = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_storePath)) return dict;

        var lines = File.ReadAllLines(_storePath); // 可能抛出 IOException
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(':');
            if (parts.Length < 2) continue; // 格式不完整则跳过（容错）
            var id = parts[0].Trim();
            var fp = parts[1].Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(fp)) continue;

            DateTime notAfter = DateTime.MinValue;
            if (parts.Length >= 3 && long.TryParse(parts[2], out var ticks))
            {
                try { notAfter = new DateTime(ticks, DateTimeKind.Utc); }
                catch (ArgumentOutOfRangeException) { /* 忽略无效刻度，保留 MinValue */ }
            }
            dict[id] = new Entry(fp, notAfter);
        }
        return dict;
    }

    private void SaveEntries(Dictionary<string, Entry> entries)
    {
        var dir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); // 可能抛出 IOException
        var lines = entries.Select(kv => $"{kv.Key}:{kv.Value.Fingerprint}:{kv.Value.NotAfterUtc.Ticks}");
        File.WriteAllLines(_storePath, lines); // 可能抛出 IOException
    }
}