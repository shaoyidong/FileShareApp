using FileShare.Core.Util;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.EdEC;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.IO.Pem;
using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Crypto
{
    /// <summary>
    /// 签名密钥，对应 Rust 端 <c>src/crypto/token.rs</c> 中的 <c>SigningTokenKey</c>。
    /// 使用 BouncyCastle 2.4.x 实现 Ed25519（.NET 10 预览版尚未原生提供 Ed25519）。
    /// </summary>
    public sealed class SigningTokenKey
    {
        private readonly Ed25519PrivateKeyParameters _privateKey;
        private readonly Ed25519PublicKeyParameters _publicKey;

        private SigningTokenKey(Ed25519PrivateKeyParameters privateKey)
        {
            _privateKey = privateKey;
            _publicKey = privateKey.GeneratePublicKey();
        }

        /// <summary>生成新的 Ed25519 签名密钥，对应 Rust 的 <c>generate_key</c>。</summary>
        public static SigningTokenKey Generate()
        {
            var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
            return new SigningTokenKey(privateKey);
        }

        /// <summary>导出对应的验签公钥。</summary>
        public IVerifyingTokenKey ToVerifyingKey()
        {
            return Ed25519VerifyingKey.FromPublicKey(_publicKey);
        }

        /// <summary>导出 PKCS#8 PEM 格式的私钥，对应 Rust 的 <c>export_private_key</c>。</summary>
        public string ExportPrivateKey()
        {
            using var sw = new StringWriter();
            using (var pemWriter = new PemWriter(sw))
            {
                var algId = new AlgorithmIdentifier(EdECObjectIdentifiers.id_Ed25519);
                var privKeyOctet = new DerOctetString(_privateKey.GetEncoded());
                var pki = new PrivateKeyInfo(algId, privKeyOctet);
                pemWriter.WriteObject(new PemObject("PRIVATE KEY", pki.GetEncoded()));
            }
            return sw.ToString();
        }

        /// <summary>从 PKCS#8 PEM 字符串解析私钥，对应 Rust 的 <c>parse_private_key</c>。</summary>
        public static SigningTokenKey ParsePrivateKey(string pem)
        {
            using var sr = new StringReader(pem);
            using var pemReader = new PemReader(sr);
            var pemObj = pemReader.ReadPemObject();
            var asn1 = Asn1Object.FromByteArray(pemObj.Content);
            var pki = PrivateKeyInfo.GetInstance(asn1);
            var keyBytes = pki.PrivateKey.GetOctets();
            var key = new Ed25519PrivateKeyParameters(keyBytes);
            return new SigningTokenKey(key);
        }

        /// <summary>导出对应公钥的 PEM 格式，对应 Rust 的 <c>export_public_key</c>。</summary>
        public string ExportPublicKey()
        {
            using var sw = new StringWriter();
            using (var pemWriter = new PemWriter(sw))
            {
                var algId = new AlgorithmIdentifier(EdECObjectIdentifiers.id_Ed25519);
                var spki = new SubjectPublicKeyInfo(algId, _publicKey.GetEncoded());
                pemWriter.WriteObject(new PemObject("PUBLIC KEY", spki.GetEncoded()));
            }
            return sw.ToString();
        }

        /// <summary>
        /// 根据 <paramref name="identifier"/>（<c>ed25519</c> 或 <c>rsa-pss</c>）解析公钥，
        /// 对应 Rust 的 <c>parse_public_key</c>。
        /// </summary>
        public static IVerifyingTokenKey ParsePublicKey(string publicKeyPem, string identifier)
        {
            return identifier switch
            {
                "ed25519" => Ed25519VerifyingKey.FromPem(publicKeyPem),
                "rsa-pss" => RsaPssVerifyingKey.FromPem(publicKeyPem),
                _ => throw new InvalidOperationException($"Unsupported key type: {identifier}")
            };
        }

        /// <summary>
        /// 对消息签名（Ed25519），返回 64 字节签名。
        /// 对应 Rust 端 <c>sign</c>。
        /// </summary>
        public byte[] Sign(byte[] message)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, _privateKey);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }

        /// <summary>
        /// 生成基于时间戳的指纹 token，对应 Rust 的 <c>generate_token_timestamp</c>。
        /// </summary>
        public string GenerateTokenTimestamp()
        {
            var timestamp = TimeUtil.UnixTimestampU64();
            var salt = BitConverter.GetBytes(timestamp); // little-endian
            return Token.GenerateTokenNonce(this, salt);
        }

        /// <summary>导出公钥的原始字节（32 字节），用于 token 哈希。</summary>
        internal byte[] GetPublicKeyDer()
        {
            return _publicKey.GetEncoded();
        }
    }
}
