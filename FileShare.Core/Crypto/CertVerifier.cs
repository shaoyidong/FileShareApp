using FileShare.Core.Util;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.EdEC;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities.IO.Pem;
using Org.BouncyCastle.X509;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FileShare.Core.Crypto
{
    /// <summary>
    /// X.509 证书验证器，对应 Rust 端 <c>src/crypto/cert.rs</c> 中的 <c>CertVerifier</c>。
    /// 使用 BouncyCastle 2.4.x 实现。
    /// </summary>
    public static class CertVerifier
    {
        /// <summary>
        /// 验证 DER 格式的自签名证书。
        /// </summary>
        public static void VerifyCertFromDer(byte[] certDer, string? publicKeyPem)
        {
            var cert = new X509Certificate(certDer);
            VerifyCertFromCert(cert, publicKeyPem);
        }

        /// <summary>
        /// 验证 PEM 格式的自签名证书，对应 Rust 的 <c>verify_cert_from_pem</c>。
        /// </summary>
        public static void VerifyCertFromPem(string certPem, string? publicKeyPem)
        {
            using var sr = new StringReader(certPem);
            using var pemReader = new PemReader(sr);
            var pemObj = pemReader.ReadPemObject();
            var cert = new X509Certificate(pemObj.Content);
            VerifyCertFromCert(cert, publicKeyPem);
        }

        /// <summary>
        /// 从 DER 证书中提取公钥并返回 PEM 字符串。
        /// </summary>
        public static string PublicKeyFromCertDer(byte[] certDer)
        {
            var certAsn1 = Asn1Object.FromByteArray(certDer);
            var spki = SubjectPublicKeyInfo.GetInstance(certAsn1);
            using var sw = new StringWriter();
            using (var pemWriter = new PemWriter(sw))
            {
                pemWriter.WriteObject(new PemObject("PUBLIC KEY", spki.GetEncoded()));
            }
            return sw.ToString();
        }

        /// <summary>
        /// 计算证书的 SHA-256 指纹（大写十六进制）。
        /// </summary>
        public static string FingerprintFromCertDer(byte[] certDer)
        {
            var hash = HashUtil.Sha256(certDer);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 验证 X509Certificate 对象，对应 Rust 的 <c>verify_cert_from_cert</c>。
        /// </summary>
        internal static void VerifyCertFromCert(X509Certificate cert, string? publicKeyPem)
        {
            // 1. 验证时间有效性
            if (DateTime.UtcNow < cert.NotBefore || DateTime.UtcNow > cert.NotAfter)
                throw new InvalidOperationException($"Certificate time validity error: notBefore={cert.NotBefore}, notAfter={cert.NotAfter}, now={DateTime.UtcNow}");

            // 2. 如果提供了公钥，验证证书公钥是否匹配
            if (publicKeyPem != null)
            {
                var expectedKey = ParsePublicKeyFromPem(publicKeyPem);
                var certPubKey = cert.GetPublicKey();
                byte[] certKeyDer;
                if (certPubKey is Ed25519PublicKeyParameters edKey)
                    certKeyDer = edKey.GetEncoded();
                else if (certPubKey is RsaKeyParameters rsaKey)
                {
                    var rsaPub = new RsaPublicKeyStructure(rsaKey.Modulus, rsaKey.Exponent);
                    certKeyDer = rsaPub.ToAsn1Object().GetEncoded();
                }
                else
                    throw new CryptographicException($"Unsupported certificate public key type: {certPubKey.GetType().Name}");

                var expectedKeyDer = expectedKey.ToDer();
                if (!CryptographicOperations.FixedTimeEquals(certKeyDer, expectedKeyDer))
                    throw new CryptographicException("Public key mismatch: certificate public key does not match provided public key");
            }

            // 3. 使用 BouncyCastle 验证自签名
            var verifier = cert.GetPublicKey();
            cert.Verify(verifier);
        }        

        /// <summary>
        /// 从 PEM 公钥解析为 IVerifyingTokenKey，对应 Rust 的 <c>parse_public_key</c>。
        /// </summary>
        private static IVerifyingTokenKey ParsePublicKeyFromPem(string publicKeyPem)
        {
            using var sr = new StringReader(publicKeyPem);
            using var pemReader = new PemReader(sr);
            var pemObj = pemReader.ReadPemObject();
            var asn1 = Asn1Object.FromByteArray(pemObj.Content);
            var spki = SubjectPublicKeyInfo.GetInstance(asn1);

            var algId = spki.Algorithm;
            if (algId.Algorithm.Equals(EdECObjectIdentifiers.id_Ed25519))
            {
                var keyBytes = spki.PublicKey.GetBytes();
                var key = new Ed25519PublicKeyParameters(keyBytes);
                return Ed25519VerifyingKey.FromPublicKey(key);
            }
            else if (algId.Algorithm.Equals(PkcsObjectIdentifiers.RsaEncryption))
            {
                var rsaDer = spki.PublicKey.GetBytes();
                var rsaObj = Asn1Object.FromByteArray(rsaDer);
                var rsaPub = RsaPublicKeyStructure.GetInstance(rsaObj);
                var key = new RsaKeyParameters(false, rsaPub.Modulus, rsaPub.PublicExponent);
                return RsaPssVerifyingKey.FromPem(publicKeyPem);
            }

            throw new CryptographicException($"Unsupported public key algorithm: {algId.Algorithm}");
        }       
    }
}
