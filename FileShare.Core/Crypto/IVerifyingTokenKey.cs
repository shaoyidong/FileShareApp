using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Crypto
{
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
}
