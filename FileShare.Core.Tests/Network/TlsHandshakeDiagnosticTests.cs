using FileShare.Core.Network.Tls;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 诊断测试：隔离 SslStream 握手与证书生成，定位 TLS 集成的具体失败点。
/// </summary>
public class TlsHandshakeDiagnosticTests
{
    [Fact]
    public async Task DirectSslStreamHandshake_WithSelfSignedCerts_Succeeds()
    {
        // Arrange：生成两张自签名证书（模拟发送方与接收方）
        var tempDir = Path.Combine(Path.GetTempPath(), "FileShareTlsDiag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var clientTls = new TlsOptions { Enabled = true, CertificateDirectory = tempDir + "\\c", FingerprintStorePath = tempDir + "\\c\\fp.txt" };
        var serverTls = new TlsOptions { Enabled = true, CertificateDirectory = tempDir + "\\s", FingerprintStorePath = tempDir + "\\s\\fp.txt" };

        var clientCert = new SelfSignedCertificateProvider(clientTls, "client-id").GetOrCreateCertificate();
        var serverCert = new SelfSignedCertificateProvider(serverTls, "server-id").GetOrCreateCertificate();

        Assert.NotNull(clientCert);
        Assert.NotNull(serverCert);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var tcpClient = await listener.AcceptTcpClientAsync();
                using var rawStream = tcpClient.GetStream();
                // 服务端校验回调通过 SslStream 构造函数设置，不能再在 SslServerAuthenticationOptions 重复设置
                var ssl = new SslStream(rawStream, false, (_, _, _, _) => true);
                var sopt = new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };
                await ssl.AuthenticateAsServerAsync(sopt);
                return (SslStream: ssl, RemoteCertificate: ssl.RemoteCertificate, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (SslStream: (SslStream?)null, RemoteCertificate: (X509Certificate?)null, Error: ex);
            }
        });

        X509Certificate? remoteCertOnClient = null;
        Exception? clientError = null;
        try
        {
            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, port);
                using var rawStream = tcpClient.GetStream();
                var ssl = new SslStream(rawStream, false, (_, cert, _, _) => { remoteCertOnClient = cert; return true; });
                var copt = new SslClientAuthenticationOptions
                {
                    TargetHost = "server-id",
                    ClientCertificates = new X509CertificateCollection { clientCert },
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };
                await ssl.AuthenticateAsClientAsync(copt);
            }
        }
        catch (Exception ex)
        {
            clientError = ex;
        }

        var serverResult = await serverTask;
        // 显式报告两端的异常，便于定位
        if (serverResult.Error != null)
            Assert.Fail($"服务端握手异常: {serverResult.Error.GetType().Name}: {serverResult.Error.Message}\n{serverResult.Error.InnerException}");
        if (clientError != null)
            Assert.Fail($"客户端握手异常: {clientError.GetType().Name}: {clientError.Message}\n{clientError.InnerException}");
        Assert.NotNull(remoteCertOnClient);
        Assert.NotNull(serverResult.RemoteCertificate);
    }
}
