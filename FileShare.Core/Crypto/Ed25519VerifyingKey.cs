using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.EdEC;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Utilities.IO.Pem;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FileShare.Core.Crypto
{
    /// <summary>
    /// Ed25519 公钥验证器，对应 Rust 的 <c>Ed25519VerifyingKey</c>。
    /// </summary>
    public sealed class Ed25519VerifyingKey : IVerifyingTokenKey
    {
        private readonly Ed25519PublicKeyParameters _key;

        public string SignatureMethod => "ed25519";

        private Ed25519VerifyingKey(Ed25519PublicKeyParameters key)
        {
            _key = key;
        }

        public static Ed25519VerifyingKey FromPublicKey(Ed25519PublicKeyParameters key)
            => new(key);

        public static Ed25519VerifyingKey FromPem(string pem)
        {
            using var sr = new StringReader(pem);
            using var pemReader = new PemReader(sr);
            var pemObj = pemReader.ReadPemObject();
            var asn1 = Asn1Object.FromByteArray(pemObj.Content);
            var spki = SubjectPublicKeyInfo.GetInstance(asn1);
            var keyBytes = spki.PublicKey.GetBytes();
            var key = new Ed25519PublicKeyParameters(keyBytes);
            return new Ed25519VerifyingKey(key);
        }

        /// <summary>使用 Ed25519 验证签名，对应 Rust 的 <c>verify</c>。</summary>
        public void Verify(byte[] message, byte[] signature)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, _key);
            verifier.BlockUpdate(message, 0, message.Length);
            if (!verifier.VerifySignature(signature))
            {
                throw new CryptographicException("Invalid Ed25519 signature");
            }
        }

        /// <summary>导出 SubjectPublicKeyInfo DER 编码，对应 Rust 的 <c>export_der</c>。</summary>
        public byte[] ToDer()
        {
            var algId = new AlgorithmIdentifier(EdECObjectIdentifiers.id_Ed25519);
            var spki = new SubjectPublicKeyInfo(algId, _key.GetEncoded());
            return spki.GetEncoded();
        }

        /// <summary>导出 PEM 公钥。</summary>
        public string ToPem()
        {
            using var sw = new StringWriter();
            using (var pemWriter = new PemWriter(sw))
            {
                var algId = new AlgorithmIdentifier(EdECObjectIdentifiers.id_Ed25519);
                var spki = new SubjectPublicKeyInfo(algId, _key.GetEncoded());
                pemWriter.WriteObject(new PemObject("PUBLIC KEY", spki.GetEncoded()));
            }
            return sw.ToString();
        }
    }
}
