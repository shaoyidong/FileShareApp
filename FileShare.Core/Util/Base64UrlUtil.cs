using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Util
{
    /// <summary>
    /// Base64 URL 安全编解码工具，对应 Rust 端 <c>src/util/base64.rs</c>。
    /// </summary>
    /// <remarks>
    /// 使用 URL 安全字母表且无填充（<c>URL_SAFE_NO_PAD</c>），与 Rust 端一致。
    /// </remarks>
    public static class Base64UrlUtil
    {
        /// <summary>将字节数组编码为 URL 安全、无填充的 Base64 字符串。</summary>
        public static string Encode(ReadOnlySpan<byte> data)
        {
            // 计算无填充时的输出长度：每 3 字节产生 4 字符，向上取整。
            int baseLen = ((data.Length + 2) / 3) * 4;
            int padLen = data.Length % 3 == 0 ? 0 : 3 - (data.Length % 3);
            int outputLen = baseLen - padLen;

            // 先用标准 Base64 编码，再替换字母表并去掉填充，
            // 这样可以避免自己实现位操作，降低出错概率。
            char[] chars = new char[baseLen];
            if (!Convert.TryToBase64Chars(data, chars, out int written, Base64FormattingOptions.None))
            {
                // 仅在缓冲区过小时失败，这里已按理论最大长度分配。
                throw new InvalidOperationException("Base64 encoding failed.");
            }

            // 将 '+' -> '-'，'/' -> '_'，并截断填充 '='。
            for (int i = 0; i < outputLen; i++)
            {
                chars[i] = chars[i] switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => chars[i]
                };
            }

            return new string(chars, 0, outputLen);
        }

        /// <summary>将 URL 安全、无填充的 Base64 字符串解码为字节数组。</summary>
        public static byte[] Decode(string data)
        {
            // 把 URL 安全字母表还原为标准字母表并补齐 4 字节倍数的填充。
            int padLen = (4 - data.Length % 4) % 4;
            char[] standard = new char[data.Length + padLen];
            for (int i = 0; i < data.Length; i++)
            {
                standard[i] = data[i] switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => data[i]
                };
            }
            for (int i = 0; i < padLen; i++)
            {
                standard[data.Length + i] = '=';
            }

            return Convert.FromBase64CharArray(standard, 0, standard.Length);
        }
    }
}
