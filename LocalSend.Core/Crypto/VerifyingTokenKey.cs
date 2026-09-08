using System.Security.Cryptography;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.EdEC;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Utilities.IO.Pem;

namespace LocalSend.Core.Crypto;

/// <summary>
/// 验证密钥，对应 Rust 端 <c>src/crypto/token.rs</c> 中的 <c>VerifyingTokenKey</c>。
/// </summary>
public interface IVerifyingTokenKey
{
    /// <summary>签名算法标识（ed25519 / rsa-pss），对应 Rust 的 <c>signature_method</c>。</summary>
    string SignatureMethod { get; }

    /// <summary>对消息进行签名验证。</summary>
    void Verify(byte[] message, byte[] signature);

    /// <summary>导出公钥的 DER 编码（SubjectPublicKeyInfo），对应 Rust 的 <c>export_der</c>。</summary>
    byte[] ToDer();

    /// <summary>导出 PEM 格式公钥，对应 Rust 的 <c>export_pem</c>。</summary>
    string ToPem();
}

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