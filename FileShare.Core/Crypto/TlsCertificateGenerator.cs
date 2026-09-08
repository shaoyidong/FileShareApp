using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FileShare.Core.Crypto
{
    /// <summary>
    /// 生成 RSA 自签名 TLS 证书，对应 Dart 端 <c>security_helper.dart</c> 中的
    /// <c>generateSecurityContext</c>。
    /// </summary>
    /// <remarks>
    /// 原版 LocalSend 使用 RSA 2048 自签名证书进行 mTLS 通信，
    /// 证书指纹（SHA-256 of DER，大写十六进制）作为设备标识。
    /// </remarks>
    public static class TlsCertificateGenerator
    {
        /// <summary>
        /// 生成自签名 RSA 证书并返回 PEM 格式的证书、私钥和指纹。
        /// </summary>
        /// <returns>
        /// (CertPem, PrivateKeyPem, Fingerprint)
        /// Fingerprint = SHA-256 of certificate DER（大写十六进制），与原版 LocalSend 一致。
        /// </returns>
        public static (string CertPem, string PrivateKeyPem, string Fingerprint) Generate()
        {
            using var rsa = RSA.Create(2048);

            var subject = new X500DistinguishedName("CN=LocalSend User");
            var req = new CertificateRequest(
                subject,
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // 10 年有效期（与 Dart 端 365*10 一致）
            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = DateTimeOffset.UtcNow.AddDays(365 * 10);

            using var cert = req.CreateSelfSigned(notBefore, notAfter);

            string certPem = cert.ExportCertificatePem();
            string keyPem = rsa.ExportRSAPrivateKeyPem();

            // 指纹 = SHA-256(cert DER) 的大写十六进制
            byte[] der = cert.Export(X509ContentType.Cert);
            string fingerprint = Convert.ToHexString(SHA256.HashData(der));

            return (certPem, keyPem, fingerprint);
        }
    }

}
