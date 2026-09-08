using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
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
    /// RSA-PSS 公钥验证器，对应 Rust 的 <c>RsaPssVerifyingKey</c>。
    /// </summary>
    public sealed class RsaPssVerifyingKey : IVerifyingTokenKey
    {
        private readonly RsaKeyParameters _key;

        public string SignatureMethod => "rsa-pss";

        private RsaPssVerifyingKey(RsaKeyParameters key)
        {
            _key = key;
        }

        public static RsaPssVerifyingKey FromPem(string pem)
        {
            using var sr = new StringReader(pem);
            using var pemReader = new PemReader(sr);
            var pemObj = pemReader.ReadPemObject();
            var asn1 = Asn1Object.FromByteArray(pemObj.Content);
            var spki = SubjectPublicKeyInfo.GetInstance(asn1);
            var rsaPublicKeyDer = spki.PublicKey.GetBytes();
            var rsaPubObj = Asn1Object.FromByteArray(rsaPublicKeyDer);
            var rsaPub = RsaPublicKeyStructure.GetInstance(rsaPubObj);
            var key = new RsaKeyParameters(false, rsaPub.Modulus, rsaPub.PublicExponent);
            return new RsaPssVerifyingKey(key);
        }

        /// <summary>使用 RSA-PSS 验证签名，对应 Rust 的 <c>verify</c>。</summary>
        public void Verify(byte[] message, byte[] signature)
        {
            var cipher = new RsaBlindedEngine();
            cipher.Init(false, _key);
            var digest = new Sha256Digest();
            var verifier = new PssSigner(cipher, digest);
            verifier.BlockUpdate(message, 0, message.Length);
            if (!verifier.VerifySignature(signature))
            {
                throw new CryptographicException("Invalid RSA-PSS signature");
            }
        }

        /// <summary>导出 SubjectPublicKeyInfo DER 编码，对应 Rust 的 <c>export_der</c>。</summary>
        public byte[] ToDer()
        {
            var algId = new AlgorithmIdentifier(PkcsObjectIdentifiers.RsaEncryption);
            var rsaPub = new RsaPublicKeyStructure(_key.Modulus, _key.Exponent);
            var spki = new SubjectPublicKeyInfo(algId, rsaPub.ToAsn1Object().GetEncoded());
            return spki.GetEncoded();
        }

        /// <summary>导出 PEM 公钥。</summary>
        public string ToPem()
        {
            using var sw = new StringWriter();
            using (var pemWriter = new PemWriter(sw))
            {
                var algId = new AlgorithmIdentifier(PkcsObjectIdentifiers.RsaEncryption);
                var rsaPub = new RsaPublicKeyStructure(_key.Modulus, _key.Exponent);
                var spki = new SubjectPublicKeyInfo(algId, rsaPub.ToAsn1Object().GetEncoded());
                pemWriter.WriteObject(new PemObject("PUBLIC KEY", spki.GetEncoded()));
            }
            return sw.ToString();
        }
    }
}
