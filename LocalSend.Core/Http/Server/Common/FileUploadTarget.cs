using System.IO;
using System.Threading.Channels;

namespace LocalSend.Core.Http.Server.Common;

/// <summary>
/// 上传文件的目标，由应用程序在收到 <see cref="V2.ServerEventV2.FileUpload"/> 事件后选择。
/// 对应 Rust 端 <c>src/http/server/common/save.rs</c> 中的 <c>FileUploadTarget</c>。
/// </summary>
public abstract class FileUploadTarget
{
    /// <summary>
    /// 应用程序自己消费上传的二进制数据块。
    /// 服务器把数据块推入 <see cref="StreamTarget.BinaryTx"/>，
    /// EOF 时关闭通道。应用程序应当比对收到的字节数与 <c>file.size</c>，
    /// 然后通过 <see cref="StreamTarget.ResultRx"/> 报告结果
    /// （200 OK / 500 失败）。
    /// </summary>
    public sealed class StreamTarget : FileUploadTarget
    {
        /// <summary>供服务器写入数据块的通道。</summary>
        public Channel<byte[]> BinaryTx { get; }

        /// <summary>应用程序报告处理结果的通道。</summary>
        public TaskCompletionSource<Result<Unit, string>> ResultRx { get; }

        public StreamTarget(Channel<byte[]> binaryTx, TaskCompletionSource<Result<Unit, string>> resultRx)
        {
            BinaryTx = binaryTx;
            ResultRx = resultRx;
        }
    }

    /// <summary>
    /// 服务器把上传内容写入磁盘文件，并把结果通过 <see cref="PathTarget.ResultTx"/> 报告。
    /// </summary>
    public sealed class PathTarget : FileUploadTarget
    {
        public string Path { get; }
        public TaskCompletionSource<Result<Unit, string>> ResultTx { get; }
        public Channel<ulong>? ProgressTx { get; }

        public PathTarget(string path, TaskCompletionSource<Result<Unit, string>> resultTx, Channel<ulong>? progressTx = null)
        {
            Path = path;
            ResultTx = resultTx;
            ProgressTx = progressTx;
        }
    }
}

/// <summary>简单的 Result 类型，对应 Rust 的 <c>Result&lt;T, E&gt;</c>。</summary>
public readonly struct Result<T, E>
{
    public bool IsOk { get; }
    public T Value { get; }
    public E Error { get; }

    public Result(T value)
    {
        IsOk = true;
        Value = value;
        Error = default!;
    }

    public Result(E error, bool _)
    {
        IsOk = false;
        Value = default!;
        Error = error;
    }

    public static Result<T, E> Ok(T value) => new(value);
    public static Result<T, E> Err(E error) => new(error, false);
}

/// <summary>单位类型，对应 Rust 的 <c>()</c>。</summary>
public readonly struct Unit { }
