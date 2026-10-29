using LocalSend.Core.Crypto;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileShare.Desktop.Services;

/// <summary>
/// LocalSend 身份（自签名 TLS 证书 + 私钥 + 指纹）。
/// <para>
/// 官方 LocalSend 用「证书 SHA-256 指纹」作为设备身份，并由对端做 TOFU 校验；
/// 这里把证书持久化到数据目录，保证重启后指纹不变（否则对端会当成新设备）。
/// </para>
/// </summary>
public sealed class LocalSendIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private LocalSendIdentity(string certPem, string privateKeyPem, string fingerprint)
    {
        CertPem = certPem;
        PrivateKeyPem = privateKeyPem;
        Fingerprint = fingerprint;
    }

    /// <summary>证书 PEM。</summary>
    public string CertPem { get; }

    /// <summary>私钥 PEM（RSA）。</summary>
    public string PrivateKeyPem { get; }

    /// <summary>证书 DER 的 SHA-256（大写十六进制），与官方一致。</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// 读取或创建本机 LocalSend 身份；文件损坏时自动重建。
    /// </summary>
    public static LocalSendIdentity LoadOrCreate(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var model = JsonSerializer.Deserialize<IdentityModel>(File.ReadAllText(filePath), JsonOptions);
                if (model is not null
                    && !string.IsNullOrWhiteSpace(model.CertPem)
                    && !string.IsNullOrWhiteSpace(model.PrivateKeyPem)
                    && !string.IsNullOrWhiteSpace(model.Fingerprint))
                {
                    return new LocalSendIdentity(model.CertPem, model.PrivateKeyPem, model.Fingerprint);
                }
            }
        }
        catch (Exception)
        {
            // 读不出来就重建
        }

        var (certPem, keyPem, fingerprint) = TlsCertificateGenerator.Generate();
        var identity = new LocalSendIdentity(certPem, keyPem, fingerprint);

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(
                new IdentityModel { CertPem = certPem, PrivateKeyPem = keyPem, Fingerprint = fingerprint },
                JsonOptions));
        }
        catch (Exception)
        {
            // 持久化失败不影响本次运行（只是重启后指纹会变）
        }

        return identity;
    }

    private sealed class IdentityModel
    {
        public string CertPem { get; set; } = string.Empty;

        public string PrivateKeyPem { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;
    }
}

/// <summary>
/// LocalSend 对端证书指纹的 TOFU 信任库（按 <c>ip:port</c> 记录）。
/// </summary>
public sealed class LocalSendTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pins;

    private LocalSendTrustStore(string filePath, Dictionary<string, string> pins)
    {
        _filePath = filePath;
        _pins = pins;
    }

    /// <summary>读取或创建信任库。</summary>
    public static LocalSendTrustStore LoadOrCreate(string filePath)
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(filePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath), JsonOptions);
                if (loaded is not null)
                {
                    foreach (var pair in loaded)
                    {
                        pins[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 损坏就当空库
        }

        return new LocalSendTrustStore(filePath, pins);
    }

    /// <summary>
    /// 用 TOFU 规则校验对端指纹。
    /// </summary>
    /// <param name="endpoint">对端 <c>ip:port</c>（信任库的 key）。</param>
    /// <param name="fingerprint">本次握手中实际看到的证书指纹。</param>
    /// <param name="known">已记录的指纹（首次为 null）。</param>
    /// <returns>是否可信、是否首次记录。</returns>
    public (bool Trusted, bool FirstSeen) Verify(string endpoint, string fingerprint)
    {
        lock (_gate)
        {
            if (_pins.TryGetValue(endpoint, out var known))
            {
                return (string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase), false);
            }

            _pins[endpoint] = fingerprint;
            Save();
            return (true, true);
        }
    }

    /// <summary>当前已信任的对端数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pins.Count;
            }
        }
    }

    /// <summary>清除全部信任记录。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pins.Clear();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_pins, JsonOptions));
        }
        catch (Exception)
        {
            // 持久化失败不影响本次运行
        }
    }
}