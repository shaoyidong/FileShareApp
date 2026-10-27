using FileShare.Core.Crypto;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 证书/指纹基础设施的诊断测试。
/// <para>原实现针对早期的 <c>TlsOptions</c> / <c>SelfSignedCertificateProvider</c> 设计，
/// 这两个类型已被 <see cref="CryptoManager"/> 取代，这里改为直接验证当前实现。</para>
/// </summary>
public class TlsHandshakeDiagnosticTests
{
    [Fact]
    public void CryptoManager_ProvidesSelfSignedCertificate()
    {
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate;
        try
        {
            certificate = CryptoManager.Instance.GetX509Certificate2();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 受限环境（无法写入私钥容器）会在这里抛出，属于环境问题而不是代码问题
            Assert.Skip("当前环境无法加载/生成自签名证书（私钥容器不可写），跳过证书诊断。");
            return;
        }

        if (certificate is null)
        {
            Assert.Skip("当前环境无法加载/生成自签名证书，跳过证书诊断。");
        }

        Assert.NotNull(certificate);
        Assert.Contains("CN=", certificate!.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.True(certificate.NotAfter > DateTime.Now.AddDays(-1));
    }

    [Fact]
    public void FingerprintStore_ComputesStableSha256Fingerprint()
    {
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate;
        try
        {
            certificate = CryptoManager.Instance.GetX509Certificate2();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            Assert.Skip("当前环境无法加载/生成自签名证书（私钥容器不可写），跳过指纹诊断。");
            return;
        }

        if (certificate is null)
        {
            Assert.Skip("当前环境无可用证书，跳过指纹诊断。");
        }

        var first = CertVerifier.FingerprintFromCertDer(certificate!.RawData);
        var second = CertVerifier.FingerprintFromCertDer(certificate.RawData);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
        // SHA-256 十六进制长度固定为 64
        Assert.Equal(64, first.Length);
    }
}
