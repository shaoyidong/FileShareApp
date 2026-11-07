using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FileShare.Crypto;

/// <summary>
/// PEM 证书 + 私钥 → <see cref="X509Certificate2"/> 的统一加载器。
/// <para>
/// 实现路径与 FileShare 自身 TLS 完全一致：BouncyCastle 纯托管组装 PFX，
/// 再经 <see cref="X509CertificateLoader"/> 导入密钥容器（用户容器 → 机器容器 → 临时文件 →
/// 内存临时密钥兜底），避免 .NET 平台 <c>CreateFromPem/CopyWithPrivateKey</c> 产生的
/// 临时密钥（ephemeral key）——Windows Schannel 与移动平台的 TLS 握手都不支持它。
/// </para>
/// </summary>
public static class TlsCertificateLoader
{
    /// <summary>最近一次 <see cref="LoadWithPrivateKey"/> 是否退化为内存临时密钥。true 表示该证书无法用于 HTTPS 握手。</summary>
    public static bool LastLoadUsedEphemeralKey { get; private set; }

    private const string DefaultPfxPassword = "FileShare_Tls_Loader_2026";

    /// <summary>
    /// 加载含私钥的证书。优先写入用户密钥容器，逐级降级，最终失败时返回内存临时密钥（不可用于服务端 TLS）。
    /// </summary>
    /// <param name="pfxPassword">组装 PFX 用的临时密码（仅存在于内存中的字节数组上）。</param>
    /// <param name="trace">可选的诊断输出（如 <c>LsLog.Trace</c>）。</param>
    public static X509Certificate2 LoadWithPrivateKey(
        string certPem,
        string privateKeyPem,
        string? pfxPassword = null,
        Action<string>? trace = null)
    {
        var password = pfxPassword ?? DefaultPfxPassword;

        // 1. BouncyCastle 纯托管组装 PFX（AOT 安全）
        byte[] pfx;
        try
        {
            var tlsCert = TlsCertificate.FromPem(certPem, privateKeyPem);
            pfx = tlsCert.ExportPfx(password);
        }
        catch (Exception ex)
        {
            trace?.Invoke($"    [证书] 组装 PFX 失败（{ex.Message}）");
            LastLoadUsedEphemeralKey = true;
            throw;
        }

        // 2. 导入用户密钥容器
        try
        {
            LastLoadUsedEphemeralKey = false;
            return X509CertificateLoader.LoadPkcs12(
                pfx, password,
                X509KeyStorageFlags.UserKeySet
                | X509KeyStorageFlags.PersistKeySet
                | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            trace?.Invoke($"    [证书] 用户密钥容器不可用（{ex.Message}），尝试机器密钥容器");
        }

        // 3. 导入机器密钥容器
        try
        {
            var machineLoaded = X509CertificateLoader.LoadPkcs12(
                pfx, password,
                X509KeyStorageFlags.MachineKeySet
                | X509KeyStorageFlags.PersistKeySet
                | X509KeyStorageFlags.Exportable);
            LastLoadUsedEphemeralKey = false;
            return machineLoaded;
        }
        catch (CryptographicException machineEx)
        {
            trace?.Invoke($"    [证书] 机器密钥容器也不可用（{machineEx.Message}），改用临时文件导入");
        }

        // 4. 临时 PFX 文件导入
        var tempPath = Path.Combine(Path.GetTempPath(), "fileshare-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            File.WriteAllBytes(tempPath, pfx);
            var loaded = X509CertificateLoader.LoadPkcs12FromFile(
                tempPath, password,
                X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
            LastLoadUsedEphemeralKey = false;
            return loaded;
        }
        catch (Exception fallbackEx)
        {
            trace?.Invoke($"    [证书] 临时文件导入也失败（{fallbackEx.Message}），退化为内存证书（HTTPS 不可用）");

            // 5. 最终兜底：内存临时密钥（Schannel/移动端不能用它做 TLS 握手）
            LastLoadUsedEphemeralKey = true;
            return X509CertificateLoader.LoadPkcs12(
                pfx, password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 清理失败可忽略 */ }
        }
    }

    /// <summary>
    /// 构建服务端 TLS 上下文（供 <see cref="System.Net.Security.SslStream"/> 的
    /// <c>SslServerAuthenticationOptions.ServerCertificateContext</c> 使用）。
    /// <para>
    /// Windows 上若直接用「临时私钥」的证书给 <c>SslStream</c>，会报
    /// “the platform does not support ephemeral keys”；
    /// 用 <see cref="SslStreamCertificateContext"/> 可以规避这个问题（无需把私钥落到磁盘）。
    /// </para>
    /// </summary>
    public static (X509Certificate2 Certificate, SslStreamCertificateContext Context) CreateServerContext(
        string certPem,
        string privateKeyPem,
        Action<string>? trace = null)
    {
        // 注意：不能 using/dispose 这个证书——SslStreamCertificateContext 会持有它的句柄，
        // 提前释放会导致 “m_safeCertContext is an invalid handle”。
        var certificate = LoadWithPrivateKey(certPem, privateKeyPem, trace: trace);
        var context = SslStreamCertificateContext.Create(certificate, additionalCertificates: null, offline: true);
        return (certificate, context);
    }
}
