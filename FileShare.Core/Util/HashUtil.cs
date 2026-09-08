using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FileShare.Core.Util
{
    /// <summary>
    /// 哈希工具，对应 Rust 端 <c>src/crypto/hash.rs</c>。
    /// </summary>
    public static class HashUtil
    {
        /// <summary>
        /// 计算数据的 SHA-256 哈希，对应 Rust 的 <c>sha256</c>。
        /// </summary>
        public static byte[] Sha256(byte[] data)
        {
            return SHA256.HashData(data);
        }

        /// <summary>计算数据的 SHA-256 哈希（只读跨度重载）。</summary>
        public static byte[] Sha256(ReadOnlySpan<byte> data)
        {
            return SHA256.HashData(data);
        }
    }
}
