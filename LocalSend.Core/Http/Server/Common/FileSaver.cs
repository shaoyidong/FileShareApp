using System.IO;
using System.Threading.Channels;

namespace LocalSend.Core.Http.Server.Common;

/// <summary>
/// 把上传请求体保存到 <see cref="FileUploadTarget"/> 的工具，
/// 对应 Rust 端 <c>src/http/server/common/save.rs</c> 中的
/// <c>save_req_to_target</c> 和 <c>write_file_from_receiver</c>。
/// </summary>
public static class FileSaver
{
    /// <summary>上传通道容量，对应 Rust 端 UPLOAD_CHANNEL_CAPACITY。</summary>
    public const int UploadChannelCapacity = 16;

    /// <summary>
    /// 从 <paramref name="input"/> 读取上传数据块并转发到 <paramref name="target"/>，
    /// 等待目标报告结果后返回是否成功。
    /// </summary>
    public static async Task<bool> SaveReqToTargetAsync(
        Stream input, FileUploadTarget target, ulong fileSize, CancellationToken ct = default)
    {
        // 把 target 解析成统一的 (binary_tx, result_task) 二元组。
        Channel<byte[]> binaryTx;
        Task<Result<Unit, string>> resultTask;
        Channel<ulong>? progressTx = null;

        switch (target)
        {
            case FileUploadTarget.StreamTarget s:
                binaryTx = s.BinaryTx;
                resultTask = s.ResultRx.Task;
                break;
            case FileUploadTarget.PathTarget p:
                var (tx, rx) = SpawnFileWriter(p.Path, fileSize, p.ResultTx);
                binaryTx = tx;
                resultTask = rx;
                progressTx = p.ProgressTx;
                break;
            default:
                throw new InvalidOperationException("Unknown FileUploadTarget");
        }

        bool streamError = false;
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                if (!await binaryTx.Writer.WaitToWriteAsync(ct).ConfigureAwait(false)
                    || !binaryTx.Writer.TryWrite(chunk))
                {
                    streamError = true;
                    break;
                }
            }
        }
        catch
        {
            streamError = true;
        }
        finally
        {
            binaryTx.Writer.TryComplete();
        }

        if (streamError)
        {
            return false;
        }

        var result = await resultTask.ConfigureAwait(false);
        return result.IsOk;
    }

    /// <summary>
    /// 启动后台任务，把接收到的数据块写入 <paramref name="path"/>。
    /// 返回写入通道与最终结果的 Task。
    /// 对应 Rust 端 <c>spawn_file_writer</c>。
    /// </summary>
    private static (Channel<byte[]> Tx, Task<Result<Unit, string>> Result) SpawnFileWriter(
        string path, ulong expectedSize, TaskCompletionSource<Result<Unit, string>> resultTcs)
    {
        var channel = Channel.CreateBounded<byte[]>(UploadChannelCapacity);
        var internalTcs = new TaskCompletionSource<Result<Unit, string>>();

        _ = Task.Run(async () =>
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                using var fs = File.Create(path);
                ulong written = 0;
                await foreach (var chunk in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    written += (ulong)chunk.Length;
                    if (written > expectedSize)
                    {
                        var err = Result<Unit, string>.Err(
                            $"Expected {expectedSize} bytes, received at least {written}");
                        resultTcs.TrySetResult(err);
                        internalTcs.TrySetResult(err);
                        return;
                    }
                    await fs.WriteAsync(chunk).ConfigureAwait(false);
                }
                await fs.FlushAsync().ConfigureAwait(false);
                if (written != expectedSize)
                {
                    var err = Result<Unit, string>.Err(
                        $"Expected {expectedSize} bytes, received {written}");
                    resultTcs.TrySetResult(err);
                    internalTcs.TrySetResult(err);
                    return;
                }
                var ok = Result<Unit, string>.Ok(default);
                resultTcs.TrySetResult(ok);
                internalTcs.TrySetResult(ok);
            }
            catch (Exception ex)
            {
                var err = Result<Unit, string>.Err(ex.Message);
                resultTcs.TrySetResult(err);
                internalTcs.TrySetResult(err);
            }
        });

        return (channel, internalTcs.Task);
    }
}
