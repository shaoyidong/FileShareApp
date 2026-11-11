namespace FileShare.LocalSend;

/// <summary>身份文件的持久化模型（字段名保持 PascalCase，兼容旧文件）。</summary>
internal sealed class IdentityModel
{
    public string CertPem { get; set; } = string.Empty;

    public string PrivateKeyPem { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;
}
