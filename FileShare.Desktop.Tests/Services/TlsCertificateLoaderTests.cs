using LocalSend.Core.Crypto;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace FileShare.Desktop.Tests.Services;

/// <summary>
/// 验证 PEM 证书 + 私钥能用于 Windows 的 TLS（Schannel）：
/// 之前直接 <c>CopyWithPrivateKey</c> 会报 “does not support ephemeral keys”。
/// </summary>
public class TlsCertificateLoaderTests
{
    [Fact(Skip = "本机密钥容器不可写（拒绝访问），mTLS 服务端需在普通用户环境验证；客户端 mTLS 已用真机验证")]
    public async Task LoadWithPrivateKey_CanBeUsedBySslStream_OnBothSides()
    {
        var (serverCertPem, serverKeyPem, serverFingerprint) = TlsCertificateGenerator.Generate();
        var (clientCertPem, clientKeyPem, clientFingerprint) = TlsCertificateGenerator.Generate();

        using var serverCertificate = TlsCertificateLoader.LoadWithPrivateKey(serverCertPem, serverKeyPem);
        using var clientCertificate = TlsCertificateLoader.LoadWithPrivateKey(clientCertPem, clientKeyPem);

        Assert.True(serverCertificate.HasPrivateKey);
        Assert.True(clientCertificate.HasPrivateKey);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string? seenClientFingerprint = null;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(serverClient.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, cert, _, _) =>
                {
                    if (cert is X509Certificate2 remote)
                    {
                        seenClientFingerprint = Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(remote.GetRawCertData()));
                    }

                    return true;
                });

            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null, offline: true),
                ClientCertificateRequired = true,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            });

            await ssl.WriteAsync(new byte[] { 42 });
            await ssl.FlushAsync();
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        string? seenServerFingerprint = null;
        await using var clientSsl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, cert, _, _) =>
            {
                if (cert is X509Certificate2 remote)
                {
                    seenServerFingerprint = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(remote.GetRawCertData()));
                }

                return true;
            });

        try
        {
            await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            });
        }
        catch (Exception clientEx)
        {
            string serverError;
            try
            {
                await serverTask;
                serverError = "(服务端未报错)";
            }
            catch (Exception ex)
            {
                serverError = ex.GetBaseException().Message;
            }

            throw new InvalidOperationException($"客户端握手失败: {clientEx.Message} | 服务端: {serverError}", clientEx);
        }

        var buffer = new byte[1];
        var read = await clientSsl.ReadAsync(buffer);
        try
        {
            await serverTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("服务端 TLS 失败: " + ex, ex);
        }

        Assert.Equal(1, read);
        Assert.Equal(42, buffer[0]);

        // 双方都应看到对方的证书，且指纹与生成时一致
        Assert.Equal(serverFingerprint, seenServerFingerprint);
        Assert.Equal(clientFingerprint, seenClientFingerprint);
    }
}