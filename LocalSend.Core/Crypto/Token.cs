using LocalSend.Core.Util;

namespace LocalSend.Core.Crypto;

/// <summary>
/// Token 生成与验证逻辑，对应 Rust 端 <c>src/crypto/token.rs</c> 中的自由函数部分。
/// </summary>
/// <remarks>
/// LocalSend 的 token 格式为：
/// <c>{hashMethod}.{hashBase64}.{saltBase64}.{signMethod}.{signatureBase64}</c>
/// <list type="bullet">
///   <item><c>hashMethod</c> 固定为 <c>"sha256"</c>。</item>
///   <item><c>hash</c> 是 <c>SHA256(publicKeyDer || salt)</c> 的 Base64-URL。</item>
///   <item><c>salt</c> 在基于时间戳的 token 中是当前 Unix 时间戳的小端 8 字节；
///         在基于 nonce 的 token 中是双方交换的 nonce。</item>
///   <item><c>signMethod</c> 取决于公钥算法（<c>ed25519</c> 或 <c>rsa-pss</c>）。</item>
///   <item><c>signature</c> 是对 <c>hash</c> 字节的签名。</item>
/// </list>
/// </remarks>
public static class Token
{
    /// <summary>
    /// 生成基于自定义 salt 的 token，对应 Rust 的 <c>generate_token_nonce</c>。
    /// </summary>
    public static string GenerateTokenNonce(SigningTokenKey key, byte[] salt)
    {
        // 公钥 DER 编码
        var verifyingKey = key.ToVerifyingKey();
        var publicKeyDer = verifyingKey.ToDer();

        // hashInput = publicKeyDer || salt
        var hashInput = new byte[publicKeyDer.Length + salt.Length];
        Buffer.BlockCopy(publicKeyDer, 0, hashInput, 0, publicKeyDer.Length);
        Buffer.BlockCopy(salt, 0, hashInput, publicKeyDer.Length, salt.Length);

        var digest = HashUtil.Sha256(hashInput);
        var signature = key.Sign(digest);

        var hashBase64 = Base64Url.Encode(digest);
        var saltBase64 = Base64Url.Encode(salt);
        var signatureBase64 = Base64Url.Encode(signature);

        return $"sha256.{hashBase64}.{saltBase64}.ed25519.{signatureBase64}";
    }

    /// <summary>
    /// 从 token 中提取签名算法标识（第 4 段），对应 Rust 的 <c>extract_signature_identifier</c>。
    /// </summary>
    public static string? ExtractSignatureIdentifier(string token)
    {
        var parts = token.Split('.');
        return parts.Length >= 4 ? parts[3] : null;
    }

    /// <summary>
    /// 验证基于时间戳的 token，对应 Rust 的 <c>verify_token_timestamp</c>。
    /// 时间戳必须不超过 1 小时。
    /// </summary>
    public static bool VerifyTokenTimestamp(IVerifyingTokenKey publicKey, string token)
    {
        return VerifyTokenWithResult(publicKey, token, salt =>
        {
            if (salt.Length != 8)
            {
                throw new InvalidOperationException("Invalid salt length");
            }
            var timestamp = BitConverter.ToUInt64(salt);
            var now = TimeUtil.UnixTimestampU64();
            if (now - timestamp > 60u * 60u)
            {
                throw new InvalidOperationException("Fingerprint timestamp expired");
            }
        });
    }

    /// <summary>
    /// 验证基于 nonce 的 token，对应 Rust 的 <c>verify_token_nonce</c>。
    /// </summary>
    public static bool VerifyTokenNonce(IVerifyingTokenKey publicKey, string token, byte[] nonce)
    {
        return VerifyTokenWithResult(publicKey, token, salt =>
        {
            if (!salt.SequenceEqual(nonce))
            {
                throw new InvalidOperationException("Invalid nonce");
            }
        });
    }

    /// <summary>
    /// 通用 token 验证逻辑，对应 Rust 的 <c>verify_token_with_result</c>。
    /// <paramref name="verifySalt"/> 用于在解码出 salt 字节后做额外校验
    /// （时间戳过期 / nonce 不匹配等）。
    /// </summary>
    public static bool VerifyTokenWithResult(
        IVerifyingTokenKey publicKey,
        string token,
        Action<byte[]> verifySalt)
    {
        var parts = token.Split('.');
        if (parts.Length < 5)
        {
            return false;
        }
        var hashMethod = parts[0];
        var hashBase64 = parts[1];
        var saltBase64 = parts[2];
        var signMethod = parts[3];
        var signatureBase64 = parts[4];

        if (hashMethod != "sha256")
        {
            return false;
        }
        if (signMethod != publicKey.SignatureMethod)
        {
            return false;
        }

        byte[] saltBytes;
        try
        {
            saltBytes = Base64Url.Decode(saltBase64);
            verifySalt(saltBytes);
        }
        catch
        {
            return false;
        }

        var publicKeyDer = publicKey.ToDer();
        var hashInput = new byte[publicKeyDer.Length + saltBytes.Length];
        Buffer.BlockCopy(publicKeyDer, 0, hashInput, 0, publicKeyDer.Length);
        Buffer.BlockCopy(saltBytes, 0, hashInput, publicKeyDer.Length, saltBytes.Length);
        var expectedDigest = HashUtil.Sha256(hashInput);

        if (Base64Url.Encode(expectedDigest) != hashBase64)
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Base64Url.Decode(signatureBase64);
        }
        catch
        {
            return false;
        }

        try
        {
            publicKey.Verify(expectedDigest, signature);
        }
        catch
        {
            return false;
        }
        return true;
    }
}
