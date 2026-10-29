using System;
using System.Security.Cryptography;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace LocalSend.Core.Crypto;

/// <summary>
/// 把 PEM 证书 + PEM 私钥组装成可用于 TLS（Schannel / SslStream）的证书。
/// <para>
/// Windows 的 Schannel 不支持「临时密钥（ephemeral key）」的证书，直接用
/// <c>X509Certificate2.CreateFromPem(...).CopyWithPrivateKey(rsa)</c> 会抛
/// <c>AuthenticationException: the platform does not support ephemeral keys</c>。
/// 这里通过导出 PFX 再导入（<c>PersistKeySet</c>）把私钥落到密钥容器，Schannel 才能使用。
/// </para>
/// </summary>
public static class TlsCertificateLoader
{
    /// <summary>最近一次加载是否退化为内存证书（无法写入密钥容器时）。</summary>
    public static bool LastLoadUsedEphemeralKey { get; private set; }

    /// <summary>
    /// 加载证书并绑定私钥。
    /// <para>
    /// Windows 的 Schannel **不支持临时密钥**（会报
    /// “the platform does not support ephemeral keys” / “安全包中没有可用的凭证”），
    /// 因此把 PFX 重新导入并把私钥持久化到用户密钥容器。
    /// 若当前环境不允许写密钥容器（某些沙箱/受限账户），退化为内存证书并记录警告，
    /// 此时 HTTPS 握手会失败，但不影响 HTTP 模式。
    /// </para>
    /// </summary>
    public static X509Certificate2 LoadWithPrivateKey(string certPem, string privateKeyPem)
    {
        using var publicCert = X509Certificate2.CreateFromPem(certPem);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        using var withKey = publicCert.CopyWithPrivateKey(rsa);
        var pfx = withKey.Export(X509ContentType.Pfx);

        try
        {
            LastLoadUsedEphemeralKey = false;
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.UserKeySet
                                  | X509KeyStorageFlags.PersistKeySet
                                  | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            LsLog.Trace($"    [证书] 用户密钥容器不可用（{ex.Message}），尝试机器密钥容器");

            try
            {
                var machineLoaded = X509CertificateLoader.LoadPkcs12(
                    pfx,
                    password: null,
                    keyStorageFlags: X509KeyStorageFlags.MachineKeySet
                                      | X509KeyStorageFlags.PersistKeySet
                                      | X509KeyStorageFlags.Exportable);
                LastLoadUsedEphemeralKey = false;
                return machineLoaded;
            }
            catch (CryptographicException machineEx)
            {
                LsLog.Trace($"    [证书] 机器密钥容器也不可用（{machineEx.Message}），改用临时文件导入");
            }

            // 备选：写到临时 PFX 再从文件导入。Windows 会为它创建临时 CAPI 密钥容器，
            // Schannel 可以使用（而 EphemeralKeySet 的临时 CNG key 不行）。
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "fileshare-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
                try
                {
                    File.WriteAllBytes(tempPath, pfx);
                    var loaded = X509CertificateLoader.LoadPkcs12FromFile(
                        tempPath,
                        password: null,
                        keyStorageFlags: X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable);
                    LastLoadUsedEphemeralKey = false;
                    return loaded;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch (Exception)
                    {
                        // 忽略清理失败
                    }
                }
            }
            catch (Exception fallbackEx)
            {
                LastLoadUsedEphemeralKey = true;
                LsLog.Trace($"    [证书] 临时文件导入也失败（{fallbackEx.Message}），退化为内存证书（HTTPS 不可用）");

                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    password: null,
                    keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            }
        }
    }

    /// <summary>
    /// 构建服务端 TLS 上下文。
    /// <para>
    /// Windows 上若直接用「临时私钥」的证书给 <c>SslStream</c>，会报
    /// “the platform does not support ephemeral keys”；
    /// 用 <see cref="SslStreamCertificateContext"/> 可以规避这个问题（无需把私钥落到磁盘）。
    /// </para>
    /// </summary>
    public static (X509Certificate2 Certificate, SslStreamCertificateContext Context) CreateServerContext(
        string certPem,
        string privateKeyPem)
    {
        // 注意：不能 using/dispose 这个证书——SslStreamCertificateContext 会持有它的句柄，
        // 提前释放会导致 “m_safeCertContext is an invalid handle”。
        var certificate = LoadWithPrivateKey(certPem, privateKeyPem);
        var context = SslStreamCertificateContext.Create(certificate, additionalCertificates: null, offline: true);
        return (certificate, context);
    }
}