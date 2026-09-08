using System;
using System.Collections.Generic;
using System.Text;

namespace FileShare.Core.Util
{
    /// <summary>
    /// 时间相关工具，对应 Rust 端 <c>src/util/time.rs</c>。
    /// </summary>
    public static class TimeUtil
    {
        /// <summary>
        /// 返回自 Unix 纪元（1970-01-01 00:00:00 UTC）以来的秒数，
        /// 对应 Rust 的 <c>unix_timestamp_u64</c>。
        /// </summary>
        public static ulong UnixTimestampU64()
        {
            return (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
