namespace LocalSend.Core.Http.Client;

/// <summary>
/// 携带远程端公钥的响应结果，对应 Rust 端 <c>ResultWithPublicKey&lt;T&gt;</c>。
/// </summary>
/// <typeparam name="T">响应体类型。</typeparam>
public sealed class ResultWithPublicKey<T>
{
    /// <summary>
    /// 从证书中提取的公钥（PEM）。
    /// 仅在 HTTPS 模式下有值；HTTP 模式下为 <c>null</c>。
    /// </summary>
    public string? PublicKey { get; init; }

    /// <summary>响应体。</summary>
    public T Body { get; init; } = default!;

    public ResultWithPublicKey(T body, string? publicKey)
    {
        Body = body;
        PublicKey = publicKey;
    }
}
