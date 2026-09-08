using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LocalSend.Core.Crypto;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// HTTP 客户端共享工具，对应 Rust 端 <c>src/http/client/mod.rs</c> 中的
/// <c>create_reqwest_client</c>、<c>verify_cert_from_res</c>、<c>upload_body</c> 等。
/// </summary>
internal static class HttpClientFactory
{
    /// <summary>
    /// 创建带客户端证书的 <see cref="HttpClient"/>，对应 Rust 的 <c>create_reqwest_client</c>。
    /// </summary>
    /// <param name="privateKeyPem">PEM 私钥（用于客户端 mTLS 证书）。</param>
    /// <param name="certPem">PEM 证书。</param>
    /// <param name="timeout">请求超时；<c>null</c> 表示使用默认值。</param>
    public static HttpClient Create(string privateKeyPem, string certPem, TimeSpan? timeout)
    {
        // 将 cert + 私钥合并成 X509Certificate2（带私钥），用于 mTLS 客户端认证。
        // 使用 CopyWithPrivateKey 确保私钥与证书关联。
        X509Certificate2 certWithKey;
        using (var cert = X509Certificate2.CreateFromPem(certPem))
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            certWithKey = cert.CopyWithPrivateKey(rsa);
        }

        var handler = new HttpClientHandler
        {
            // LocalSend 的对端证书是自签名且指纹由设备指纹标识，
            // 因此我们手动校验（详见 VerifyRemoteCert），这里跳过框架的证书校验。
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            ClientCertificates = { certWithKey }
        };

        var client = new HttpClient(handler);
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }
        return client;
    }

    /// <summary>
    /// 创建不带客户端证书的 <see cref="HttpClient"/>，对应 Rust 的
    /// <c>LsHttpClientV2::try_new_without_cert</c>。
    /// </summary>
    public static HttpClient CreateWithoutCert(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var client = new HttpClient(handler);
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }
        return client;
    }

    /// <summary>
    /// 把 <see cref="FileContent"/> 转换为 <see cref="HttpContent"/>，
    /// 并在每读一个数据块后调用 <paramref name="progress"/> 报告累计字节数。
    /// 对应 Rust 端 <c>upload_body</c>。
    /// </summary>
    public static HttpContent UploadBody(FileContent content, Action<ulong> progress)
    {
        var channel = content.IntoReceiver();
        var stream = new ChunkedStream(channel, progress);
        return new StreamContent(stream);
    }

    /// <summary>
    /// 从 <see cref="HttpResponseMessage"/> 中提取并验证对端证书，
    /// 返回公钥的 PEM 表示。对应 Rust 端 <c>verify_cert_from_res</c>。
    /// </summary>
    /// <remarks>
    /// .NET 的 <see cref="HttpClientHandler"/> 在 <c>ServerCertificateCustomValidationCallback</c>
    /// 中可拿到对端证书，但这里我们简化处理：仅当 <paramref name="expectedPublicKey"/>
    /// 提供时校验它，否则使用请求时缓存的证书。
    /// 实际项目中可以使用 <c>SslStream</c> 自定义验证回调获取更细粒度控制。
    /// </remarks>
    public static string? VerifyRemoteCert(
        X509Certificate2? remoteCert,
        string? expectedPublicKey)
    {
        if (remoteCert == null && expectedPublicKey == null)
        {
            return null;
        }
        if (remoteCert == null)
        {
            // 没有 TLS 通道时直接返回期望的公钥。
            return expectedPublicKey;
        }

        // 校验证书签名、时间有效性、公钥匹配。
        var der = remoteCert.RawData;
        CertVerifier.VerifyCertFromDer(der, expectedPublicKey);
        return expectedPublicKey ?? CertVerifier.PublicKeyFromCertDer(der);
    }
}

/// <summary>
/// 把 <see cref="Channel{Byte}"/> 适配为可读 <see cref="Stream"/>，
/// 供 <see cref="StreamContent"/> 上传使用。
/// </summary>
internal sealed class ChunkedStream : Stream
{
    private readonly System.Threading.Channels.Channel<byte[]> _channel;
    private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
    private readonly Action<ulong> _progress;
    private byte[] _current = Array.Empty<byte>();
    private int _offset;
    private ulong _sent;

    public ChunkedStream(System.Threading.Channels.Channel<byte[]> channel, Action<ulong> progress)
    {
        _channel = channel;
        _reader = channel.Reader;
        _progress = progress;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (_offset >= _current.Length)
        {
            if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0; // EOF
            }
            if (!_reader.TryRead(out _current))
            {
                continue;
            }
            _offset = 0;
            _sent += (ulong)_current.Length;
            _progress(_sent);
        }

        int n = Math.Min(count, _current.Length - _offset);
        Buffer.BlockCopy(_current, _offset, buffer, offset, n);
        _offset += n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _channel.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }
}
