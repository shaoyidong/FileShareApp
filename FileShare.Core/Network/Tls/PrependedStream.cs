using System.IO;

namespace FileShare.Core.Network.Tls;

/// <summary>
/// 在底层流前面"预挂"若干字节的包装流。
/// <para>用于 TLS 协商：接收方读取首字节判断是否为 TLS 升级哨兵（0x16），
/// 若不是（说明是普通 TCP 对端），需把已读字节"放回"流首，供后续协议帧读取。</para>
/// <para>读取时先返回预挂字节，耗尽后委托给底层流；写入/刷新直接委托。</para>
/// </summary>
internal sealed class PrependedStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _prefixPos;

    public PrependedStream(Stream inner, byte[] prefix)
    {
        _inner = inner;
        _prefix = prefix;
        _prefixPos = 0;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixPos < _prefix.Length)
        {
            var n = Math.Min(count, _prefix.Length - _prefixPos);
            Array.Copy(_prefix, _prefixPos, buffer, offset, n);
            _prefixPos += n;
            return n;
        }
        return _inner.Read(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_prefixPos < _prefix.Length)
        {
            var n = Math.Min(count, _prefix.Length - _prefixPos);
            Array.Copy(_prefix, _prefixPos, buffer, offset, n);
            _prefixPos += n;
            return n;
        }
        return await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPos < _prefix.Length)
        {
            var n = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
            _prefix.AsSpan(_prefixPos, n).CopyTo(buffer.Span);
            _prefixPos += n;
            return new ValueTask<int>(n);
        }
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
