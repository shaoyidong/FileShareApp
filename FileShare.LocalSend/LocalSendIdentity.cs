using FileShare.Crypto;
using System.Text.Json;

namespace FileShare.LocalSend;

/// <summary>
/// LocalSend 身份（自签名 TLS 证书 + 私钥 + 指纹）。
/// <para>
/// 官方 LocalSend 用「证书 SHA-256 指纹」作为设备身份，并由对端做 TOFU 校验；
/// 这里把证书持久化到数据目录，保证重启后指纹不变（否则对端会当成新设备）。
/// </para>
/// <para>持久化走源生成 JSON 元数据，桌面端 AOT / TrimMode=full 下不会触发反射序列化。</para>
/// </summary>
public sealed class LocalSendIdentity
{
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
                var model = JsonSerializer.Deserialize(
                    File.ReadAllText(filePath), LocalSendIdentityJsonContext.Default.IdentityModel);
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

        // 统一走 FileShare.Crypto 的 BouncyCastle 证书生成（AOT 安全，指纹算法与官方一致）
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
                LocalSendIdentityJsonContext.Default.IdentityModel));
        }
        catch (Exception)
        {
            // 持久化失败不影响本次运行（只是重启后指纹会变）
        }

        return identity;
    }
}

/// <summary>身份文件的持久化模型（字段名保持 PascalCase，兼容旧文件）。</summary>
internal sealed class IdentityModel
{
    public string CertPem { get; set; } = string.Empty;

    public string PrivateKeyPem { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;
}
