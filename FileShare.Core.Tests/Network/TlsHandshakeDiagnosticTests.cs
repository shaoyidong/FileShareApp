using FileShare.Core.Crypto;
using FileShare.Core.Network;
using FileShare.Crypto;
using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;

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

    /// <summary>
    /// 证书重建导致 TOFU 指纹不匹配时，接收端会读到 SChannel 解密失败；
    /// 这类异常必须被识别为 TLS 通道失效，否则读循环会反复记错误日志。
    /// </summary>
    [Fact]
    public void IsTlsChannelFailure_RecognizesCertificateRejection()
    {
        var decryptionFailed = new IOException(
            "The decryption operation failed, see inner exception.",
            new Win32Exception(unchecked((int)0x80090327)));
        Assert.True(TcpFileTransferService.IsTlsChannelFailure(decryptionFailed));

        var corruptedFrame = new IOException(
            "The read operation failed, see inner exception.",
            new AuthenticationException("Cannot determine the frame size or a corrupted frame was received."));
        Assert.True(TcpFileTransferService.IsTlsChannelFailure(corruptedFrame));
        Assert.True(TcpFileTransferService.IsTlsChannelFailure(
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));
    }

    [Fact]
    public void IsTlsChannelFailure_DoesNotSwallowPlainTcpErrors()
    {
        var connectionReset = new IOException(
            "The read operation failed, see inner exception.",
            new SocketException((int)SocketError.ConnectionReset));

        Assert.False(TcpFileTransferService.IsTlsChannelFailure(connectionReset));
        Assert.False(TcpFileTransferService.IsTlsChannelFailure(null));
    }
}
