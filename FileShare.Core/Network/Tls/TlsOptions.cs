namespace FileShare.Core.Network.Tls;

/// <summary>
/// TLS 加密传输的可选配置。
/// <para>启用后：本设备生成/加载自签名证书，并在发现协议中广播 SupportsTls；
/// 与同样启用 TLS 的对端传输时自动升级到 SslStream，证书指纹采用 TOFU（首次信任）策略。</para>
/// <para>未启用（默认）：保持裸 TCP，与旧版本完全兼容。</para>
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// 是否启用 TLS。默认 false，保持向后兼容。
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 证书持久化目录（PFX 文件存放位置）。
    /// </summary>
    public required string CertificateDirectory { get; init; }

    /// <summary>
    /// 指纹信任库持久化文件路径（TOFU 存储：deviceId → SHA256 指纹）。
    /// </summary>
    public required string FingerprintStorePath { get; init; }

    /// <summary>
    /// 证书密码（保护 PFX 文件）。建议使用机器级 DPAPI 或随机生成并持久化。
    /// 为简化首版实现，此处使用固定密码；生产环境可改为 DPAPI 加密。
    /// </summary>
    public string CertificatePassword { get; init; } = "FileShare_Tls_Cert_2026";

    /// <summary>
    /// 证书有效期（天）。默认 365 天，过期后自动重新生成。
    /// </summary>
    public int CertificateValidityDays { get; init; } = 365;
}
