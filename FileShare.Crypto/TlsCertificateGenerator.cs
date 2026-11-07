using System;
using System.Security.Cryptography;

namespace FileShare.Crypto
{
    /// <summary>
    /// 生成 RSA 自签名 TLS 证书，对应 LocalSend Dart 端 <c>security_helper.dart</c> 中的
    /// <c>generateSecurityContext</c>。
    /// </summary>
    /// <remarks>
    /// 原版 LocalSend 使用 RSA 2048 自签名证书进行 mTLS 通信，
    /// 证书指纹（SHA-256 of DER，大写十六进制）作为设备标识。
    /// 实现全部委托给 <see cref="TlsCertificate"/>（BouncyCastle 纯托管路径），
    /// 不使用 <c>CertificateRequest</c>/平台密钥库，NativeAOT / 移动平台安全。
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
            var tlsCert = TlsCertificate.Generate();
            var (certPem, keyPem) = tlsCert.ExportPem();
            return (certPem, keyPem, tlsCert.Fingerprint);
        }
    }
}
