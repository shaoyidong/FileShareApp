using System.Buffers;
using System.IO;
using System.Threading.Channels;

namespace LocalSend.Core.Model;

/// <summary>
/// 文件内容来源，对应 Rust 端 <c>src/model/transfer.rs</c> 中的 <c>FileContent</c>。
/// </summary>
/// <remarks>
/// 应用程序可以通过以下三种方式提供文件内容：
/// <list type="bullet">
///   <item><see cref="Stream"/>：直接由内存中的字节流序列推送数据块。</item>
///   <item><see cref="Path"/>：从磁盘上的常规文件读取。</item>
///   <item><see cref="Fd"/>：从原始文件描述符读取（仅 Android，C# 移植版未实现）。</item>
/// </list>
/// </remarks>
public abstract class FileContent
{
    /// <summary>
    /// 将任意 <see cref="FileContent"/> 归一化为一个 <c>Channel&lt;byte[]&gt;</c>，
    /// 对应 Rust 的 <c>FileContent::into_receiver</c>。
    /// 调用方从返回的通道中读取数据块，直到通道关闭（EOF）。
    /// </summary>
    public abstract Channel<byte[]> IntoReceiver();

    /// <summary>从磁盘文件路径读取内容的实现。</summary>
    public sealed class FilePath : FileContent
    {
        /// <summary>文件路径。</summary>
        public string Path { get; }

        public FilePath(string path)
        {
            Path = path;
        }

        public override Channel<byte[]> IntoReceiver()
        {
            // 通道容量 16，与 Rust 端 FILE_CHANNEL_CAPACITY 保持一致，
            // 提供必要的背压而不会占用过多内存。
            var channel = Channel.CreateBounded<byte[]>(16);

            // 后台任务异步读取文件并向通道写入数据块，EOF / 出错时关闭通道。
            _ = Task.Run(async () =>
            {
                try
                {
                    using var fs = File.OpenRead(Path);
                    // 64 KiB 缓冲，对应 Rust 实现里的 bytes::BytesMut::with_capacity(64 * 1024)。
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        ulong total = 0;
                        while (true)
                        {
                            int n = await fs.ReadAsync(buffer);
                            if (n == 0)
                            {
                                break;
                            }
                            var chunk = new byte[n];
                            Array.Copy(buffer, chunk, n);
                            await channel.Writer.WriteAsync(chunk);
                            total += (ulong)n;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (Exception ex)
                {
                    // 读取失败：以异常完成通道，让下游感知错误。
                    channel.Writer.TryComplete(ex);
                    return;
                }
                channel.Writer.TryComplete();
            });

            return channel;
        }
    }

    /// <summary>
    /// 直接从字节流通道推送数据。调用方负责向 <paramref name="source"/> 写入数据并在结束时关闭。
    /// </summary>
    public sealed class FromStream : FileContent
    {
        private readonly Channel<byte[]> _source;

        public FromStream(Channel<byte[]> source)
        {
            _source = source;
        }

        public override Channel<byte[]> IntoReceiver()
        {
            return _source;
        }
    }

    /// <summary>
    /// 基于原始文件描述符（Android only）的内容来源。
    /// </summary>
    /// <remarks>
    /// .NET 移植版未实现该路径，因为 .NET 不直接暴露 POSIX 文件描述符。
    /// 保留类型用于对照 Rust 源码；调用会抛出 <see cref="PlatformNotSupportedException"/>。
    /// </remarks>
    public sealed class FromFd : FileContent
    {
        public int Fd { get; }

        public FromFd(int fd)
        {
            Fd = fd;
        }

        public override Channel<byte[]> IntoReceiver()
        {
            throw new PlatformNotSupportedException(
                "FileContent::Fd is only supported on Android in the original Rust crate.");
        }
    }
}
