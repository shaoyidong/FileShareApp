using System.Net;
using LocalSend.Core.Util;

namespace LocalSend.Core.Http.Server.Common;

/// <summary>
/// PIN 校验工具，对应 Rust 端 <c>src/http/server/common/pin.rs</c>。
/// </summary>
/// <remarks>
/// 每个 IP 最多允许 <see cref="MaxPinAttempts"/> 次失败尝试；超过后返回 429。
/// </remarks>
public static class PinChecker
{
    /// <summary>最大失败 PIN 尝试次数。</summary>
    public const int MaxPinAttempts = 3;

    /// <summary>
    /// 校验 <paramref name="query"/> 中 <c>pin</c> 是否与 <paramref name="requiredPin"/> 匹配。
    /// </summary>
    public static async Task CheckPinAsync(
        string? requiredPin,
        SemaphoreSlim pinAttemptsLock,
        LruCache<IPAddress, uint> pinAttempts,
        IReadOnlyDictionary<string, string> query,
        IPAddress ip)
    {
        if (requiredPin == null)
        {
            return;
        }

        await pinAttemptsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Peek 返回值类型是 uint，找不到时为 0。
            uint count = pinAttempts.Peek(ip) is { } c ? c : 0;
            if (count >= MaxPinAttempts)
            {
                throw AppError.WithMessage(HttpStatusCode.TooManyRequests, "Too many requests");
            }

            if (query.TryGetValue("pin", out var provided) && provided == requiredPin)
            {
                pinAttempts.Remove(ip);
                return;
            }

            pinAttempts.Put(ip, count + 1);
            throw AppError.WithMessage(
                HttpStatusCode.Unauthorized,
                string.IsNullOrEmpty(provided) ? "PIN required" : "Invalid PIN");
        }
        finally
        {
            pinAttemptsLock.Release();
        }
    }
}
