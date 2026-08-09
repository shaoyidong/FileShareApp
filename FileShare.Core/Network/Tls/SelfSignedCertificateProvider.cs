using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace FileShare.Core.Network.Tls;

/// <summary>
/// 自签名证书的生成与加载。
/// <para>每个设备首次启用 TLS 时生成一张 RSA 2048 自签名证书（CN=设备ID，含服务器认证 EKU），
/// 持久化为 PFX 文件，后续直接加载。AOT 兼容（CertificateRequest 无反射 Emit）。</para>
/// </summary>
public sealed class SelfSignedCertificateProvider
{
    private readonly TlsOptions _options;
    private readonly string _deviceId;
    private readonly ILogger<SelfSignedCertificateProvider>? _logger;

    public SelfSignedCertificateProvider(TlsOptions options, string deviceId, ILogger<SelfSignedCertificateProvider>? logger = null)
    {
        _options = options;
        _deviceId = deviceId;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前设备的 TLS 证书：优先从 PFX 文件加载，不存在或过期则生成新证书并持久化。
    /// <para>关键：生成后必须【从 PFX 重新加载】（带 PersistKeySet），使私钥进入 Windows 密钥存储。
    /// CertificateRequest.CreateSelfSigned 返回的证书持有临时内存密钥，Windows Schannel 无法用于 TLS 握手，
    /// 必须持久化后重新加载才能在 Windows 上正常工作（Linux/macOS 使用 OpenSSL，内存密钥可用，重载无害）。</para>
    /// </summary>
    public X509Certificate2 GetOrCreateCertificate()
    {
        Directory.CreateDirectory(_options.CertificateDirectory);
        var pfxPath = GetPfxPath();

        if (TryLoadCertificate(pfxPath, out var cert))
        {
            return cert;
        }

        _logger?.LogInformation("未找到有效证书，生成新的自签名证书（CN={DeviceId}）", _deviceId);
        var newCert = GenerateCertificate();
        PersistCertificate(pfxPath, newCert);
        newCert.Dispose();

        // 从 PFX 重新加载，使私钥进入密钥存储（Windows Schannel 需要）
        if (TryLoadCertificate(pfxPath, out var reloaded))
        {
            return reloaded;
        }
        // 重载失败（极端情况）：回退到内存证书（Linux/macOS 可用）
        return GenerateCertificate();
    }

    private string GetPfxPath() => Path.Combine(_options.CertificateDirectory, $"fileshare-{SanitizeForFileName(_deviceId)}.pfx");

    private bool TryLoadCertificate(string pfxPath, out X509Certificate2 cert)
    {
        cert = null!;
        if (!File.Exists(pfxPath)) return false;

        try
        {
            // 使用 X509CertificateLoader（.NET 9+ 推荐 API，AOT 友好）替代过时的 X509Certificate2 构造函数。
            // PersistKeySet：私钥持久化到密钥存储（Windows Schannel 握手所需）；
            // Exportable：允许导出（用于后续重载/迁移）；UserKeySet：写入当前用户存储，避免需要管理员权限。
            cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, _options.CertificatePassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            // 检查有效期：若已过期，重新生成
            if (DateTime.UtcNow >= cert.NotAfter || DateTime.UtcNow < cert.NotBefore)
            {
                _logger?.LogInformation("证书已过期，重新生成");
                cert.Dispose();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "加载证书失败，将重新生成");
            return false;
        }
    }

    private X509Certificate2 GenerateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={_deviceId}");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // 服务器认证 EKU（OID 1.3.6.1.5.5.7.3.1）
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));

        // 基本约束：非 CA
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(_options.CertificateValidityDays);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private void PersistCertificate(string pfxPath, X509Certificate2 cert)
    {
        try
        {
            var pfxBytes = cert.Export(X509ContentType.Pfx, _options.CertificatePassword);
            File.WriteAllBytes(pfxPath, pfxBytes);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "持久化证书失败");
        }
    }

    private static string SanitizeForFileName(string deviceId)
    {
        // 设备 ID 通常是 GUID，但防御性处理非法文件名字符
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(deviceId.Length);
        foreach (var c in deviceId)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
