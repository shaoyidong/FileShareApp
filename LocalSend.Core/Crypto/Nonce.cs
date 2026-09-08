using System.Security.Cryptography;

namespace LocalSend.Core.Crypto;

/// <summary>
/// Nonce（一次性随机数）工具，对应 Rust 端 <c>src/crypto/nonce.rs</c>。
/// </summary>
public static class Nonce
{
    /// <summary>
    /// 生成 32 字节的随机 nonce，对应 Rust 的 <c>generate_nonce</c>。
    /// </summary>
    public static byte[] GenerateNonce()
    {
        var nonce = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);
        return nonce;
    }

    /// <summary>
    /// 校验 nonce 长度是否在 [16, 128] 字节范围内，对应 Rust 的 <c>validate_nonce</c>。
    /// </summary>
    public static bool ValidateNonce(byte[] nonce)
    {
        return nonce.Length >= 16 && nonce.Length <= 128;
    }
}
