using System.Net;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Server.V2;

/// <summary>
/// v2 上传会话的文件状态，对应 Rust 端 <c>src/http/server/common/session.rs</c> 中的 <c>FileStatusV2</c>。
/// </summary>
public enum FileStatusV2
{
    Pending,
    InProgress,
    Finished,
    Failed
}

/// <summary>
/// v2 上传会话中的单个文件，对应 Rust 端 <c>SessionFileV2</c>。
/// </summary>
public class SessionFileV2
{
    /// <summary>文件元数据。</summary>
    public FileDto Dto { get; set; } = new();

    /// <summary>该文件专属的上传 token。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>当前状态。</summary>
    public FileStatusV2 Status { get; set; }
}

/// <summary>
/// 已经被接受的 v2 上传会话，对应 Rust 端 <c>UploadSessionV2</c>。
/// </summary>
public class UploadSessionV2
{
    /// <summary>会话 ID。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>发送端 IP；只允许该 IP 上传。</summary>
    public IPAddress SenderIp { get; set; } = IPAddress.None;

    /// <summary>被接受的文件，键为 file ID。</summary>
    public Dictionary<string, SessionFileV2> Files { get; set; } = new();

    /// <summary>是否所有文件都到了终态（完成或失败）。</summary>
    public bool IsComplete() => Files.Values.All(f =>
        f.Status == FileStatusV2.Finished || f.Status == FileStatusV2.Failed);
}

/// <summary>
/// v2 上传会话槽位的状态，对应 Rust 端 <c>SessionStateV2</c>。
/// 服务器同一时刻只允许一个会话：
/// <list type="bullet">
///   <item><see cref="Pending"/>：prepare-upload 正在等待应用决策。</item>
///   <item><see cref="Active"/>：已被接受，正在接收上传。</item>
/// </list>
/// </summary>
public abstract class SessionStateV2
{
    public sealed class PendingState : SessionStateV2 { }
    public sealed class ActiveState : SessionStateV2
    {
        public UploadSessionV2 Session { get; set; } = new();
    }
}
