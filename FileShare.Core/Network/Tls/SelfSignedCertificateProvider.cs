using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace FileShare.Core.Network.Tls;

/// <summary>
/// 自签名证书的生成与加载（基于 BouncyCastle 实现，支持 AOT 与跨平台）。
/// <para>每个设备首次启用 TLS 时生成一张 RSA 2048 自签名证书（CN=设备ID，含服务器认证 EKU），
/// 持久化为 PFX 文件，后续直接加载。</para>
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
        GenerateAndPersistCertificate(pfxPath);

        if (TryLoadCertificate(pfxPath, out var reloaded))
        {
            return reloaded;
        }

        // 重载失败（极端情况）：回退到内存证书
        return GenerateCertificateInMemory();
    }

    private string GetPfxPath() => Path.Combine(_options.CertificateDirectory, $"fileshare-{SanitizeForFileName(_deviceId)}.pfx");

    private bool TryLoadCertificate(string pfxPath, out X509Certificate2 cert)
    {
        cert = null!;
        if (!File.Exists(pfxPath)) return false;

        try
        {
            // 使用 X509CertificateLoader（.NET 9+ 推荐 API）加载 PKCS#12 证书
            // PersistKeySet：私钥持久化到密钥存储（Windows Schannel 握手所需）；
            // Exportable：允许导出（用于后续重载/迁移）；UserKeySet：写入当前用户存储，避免需要管理员权限。
            cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, _options.CertificatePassword,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

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

    private void GenerateAndPersistCertificate(string pfxPath)
    {
        try
        {
            // 1. 生成 RSA 密钥对 (2048 位)
            var rsaKeyPair = GenerateRsaKeyPair(2048);

            // 2. 创建证书
            var certificate = GenerateBouncyCastleCertificate(rsaKeyPair);

            // 3. 导出为 PFX 字节数组
            var pfxBytes = ExportToPfx(certificate, rsaKeyPair.Private, _options.CertificatePassword);

            // 4. 写入文件
            File.WriteAllBytes(pfxPath, pfxBytes);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "生成并持久化证书失败");
            throw;
        }
    }

    private X509Certificate2 GenerateCertificateInMemory()
    {
        var rsaKeyPair = GenerateRsaKeyPair(2048);
        var certificate = GenerateBouncyCastleCertificate(rsaKeyPair);
        var pfxBytes = ExportToPfx(certificate, rsaKeyPair.Private, _options.CertificatePassword);
        // 内存中使用 X509CertificateLoader 加载
        return X509CertificateLoader.LoadPkcs12(pfxBytes, _options.CertificatePassword,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>
    /// 使用 BouncyCastle 生成 RSA 密钥对
    /// </summary>
    private static AsymmetricCipherKeyPair GenerateRsaKeyPair(int strength)
    {
        var keyGenerationParameters = new KeyGenerationParameters(new SecureRandom(), strength);
        var keyPairGenerator = new RsaKeyPairGenerator();
        keyPairGenerator.Init(keyGenerationParameters);
        return keyPairGenerator.GenerateKeyPair();
    }

    /// <summary>
    /// 使用 BouncyCastle 生成 X509 自签名证书
    /// </summary>
    private Org.BouncyCastle.X509.X509Certificate GenerateBouncyCastleCertificate(AsymmetricCipherKeyPair keyPair)
    {
        var random = new SecureRandom();

        // 序列号：8 字节随机数
        var serialNumber = GenerateSerialNumber(random);

        var subjectName = new X509Name($"CN={_deviceId}");
        var issuerName = subjectName; // 自签名：颁发者 = 主体

        var notBefore = DateTime.UtcNow.AddDays(-1);
        var notAfter = DateTime.UtcNow.AddDays(_options.CertificateValidityDays);

        // 证书生成器
        var certificateGenerator = new X509V3CertificateGenerator();
        certificateGenerator.SetSerialNumber(serialNumber);
        certificateGenerator.SetSubjectDN(subjectName);
        certificateGenerator.SetIssuerDN(issuerName);
        certificateGenerator.SetNotBefore(notBefore);
        certificateGenerator.SetNotAfter(notAfter);
        certificateGenerator.SetPublicKey(keyPair.Public);

        // 添加服务器认证 EKU (OID: 1.3.6.1.5.5.7.3.1)
        // 添加服务器认证 EKU 和客户端认证 EKU
        //var serverAuthOid = new DerObjectIdentifier("1.3.6.1.5.5.7.3.1");
        //var clientAuthOid = new DerObjectIdentifier("1.3.6.1.5.5.7.3.2");
        //var ekuExtension = new ExtendedKeyUsage(new[] { serverAuthOid, clientAuthOid });
        var ekuExtension = new ExtendedKeyUsage([KeyPurposeID.id_kp_serverAuth ,KeyPurposeID.id_kp_clientAuth]);
        certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage, false, ekuExtension);

        // 基本约束：非 CA
        var basicConstraints = new BasicConstraints(false);
        certificateGenerator.AddExtension(X509Extensions.BasicConstraints, true, basicConstraints);

        // 使用者密钥标识符 (SKI) - 使用 X509ExtensionUtilities 避免过时警告
        var ski = Org.BouncyCastle.X509.Extension.X509ExtensionUtilities.CreateSubjectKeyIdentifier(keyPair.Public);
        certificateGenerator.AddExtension(X509Extensions.SubjectKeyIdentifier, false, ski);

        // 签名工厂：SHA256withRSA
        var signatureFactory = new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random);

        return certificateGenerator.Generate(signatureFactory);
    }

    /// <summary>
    /// 生成随机序列号（8字节，正整数）
    /// </summary>
    private static BigInteger GenerateSerialNumber(SecureRandom random)
    {
        // 生成 64 位（8 字节）随机正整数
        var serialNumber = new BigInteger(63, random);
        // 确保不为零或负数
        if (serialNumber.SignValue <= 0)
        {
            serialNumber = serialNumber.Add(BigInteger.One);
        }
        return serialNumber;
    }

    /// <summary>
    /// 将 BouncyCastle 证书和私钥导出为 PFX (PKCS#12) 字节数组
    /// </summary>
    private static byte[] ExportToPfx(Org.BouncyCastle.X509.X509Certificate certificate, AsymmetricKeyParameter privateKey, string password)
    {
        // BouncyCastle 2.x 使用 Pkcs12StoreBuilder 创建 Pkcs12Store
        var storeBuilder = new Pkcs12StoreBuilder();
        var store = storeBuilder.Build();

        var alias = certificate.SubjectDN.ToString();
        var certificateEntry = new X509CertificateEntry(certificate);

        // 设置证书条目
        store.SetCertificateEntry(alias, certificateEntry);

        // 设置私钥条目（关联证书链）
        store.SetKeyEntry(alias, new AsymmetricKeyEntry(privateKey), new[] { certificateEntry });

        using var memoryStream = new MemoryStream();
        store.Save(memoryStream, password.ToCharArray(), new SecureRandom());
        return memoryStream.ToArray();
    }

    private static string SanitizeForFileName(string deviceId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(deviceId.Length);
        foreach (var c in deviceId)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
