using System.Net;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Server.V2;

/// <summary>
/// v2 HTTP 服务器向应用程序发出的事件，对应 Rust 端
/// <c>src/http/server/v2.rs</c> 中的 <c>ServerEventV2</c>。
/// </summary>
public abstract class ServerEventV2
{
    /// <summary>
    /// 设备通过 <c>POST /api/localsend/v2/register</c> 注册自身。
    /// 在 TLS 下，仅当 <c>info.fingerprint</c> 与 mTLS 握手中验证的客户端证书
    /// 的 SHA-256 一致时才会发出本事件，避免指纹被伪造。
    /// </summary>
    public sealed class Register : ServerEventV2
    {
        public IPAddress Ip { get; init; }
        public RegisterDtoV2 Info { get; init; } = new();
    }

    /// <summary>
    /// 发送端通过 <c>POST /api/localsend/v2/prepare-upload</c> 请求上传。
    /// 应用必须通过 <see cref="DecisionTx"/> 给出回应（接受 / 部分接受 / 拒绝）。
    /// </summary>
    public sealed class PrepareUpload : ServerEventV2
    {
        /// <summary>会话被接受后使用的 ID（预先生成以便应用一致追踪）。</summary>
        public string SessionId { get; init; } = string.Empty;
        public IPAddress Ip { get; init; } = IPAddress.None;
        public RegisterDtoV2 Info { get; init; } = new();
        /// <summary>mTLS 客户端证书的 SHA-256 大写十六进制；非 TLS 模式下为 null。</summary>
        public string? CertFingerprint { get; init; }
        public Dictionary<string, FileDto> Files { get; init; } = new();
        /// <summary>用于回传决策。</summary>
        public TaskCompletionSource<PrepareUploadDecisionV2> DecisionTx { get; init; } = new();
    }

    /// <summary>
    /// 被接受的文件正在被上传（<c>POST /api/localsend/v2/upload</c>）。
    /// 应用必须通过 <see cref="TargetTx"/> 给出文件保存目标。
    /// </summary>
    public sealed class FileUpload : ServerEventV2
    {
        public string SessionId { get; init; } = string.Empty;
        public string FileId { get; init; } = string.Empty;
        public FileDto File { get; init; } = new();
        public TaskCompletionSource<FileUploadTarget> TargetTx { get; init; } = new();
    }

    /// <summary>上传会话结束。</summary>
    public sealed class SessionEnd : ServerEventV2
    {
        public string SessionId { get; init; } = string.Empty;
        public SessionEndReasonV2 Reason { get; init; }
    }

    /// <summary>
    /// prepare-upload 在创建会话之前被中止（例如发送端在应用决策时断开）。
    /// </summary>
    public sealed class PrepareUploadAborted : ServerEventV2
    {
        public string SessionId { get; init; } = string.Empty;
    }

    /// <summary>
    /// 收到 <c>POST /api/localsend/v2/cancel</c>，但本机没有对应会话。
    /// 这通常发生在远端正在取消我方主动发起的发送会话。
    /// </summary>
    public sealed class CancelReceived : ServerEventV2
    {
        public IPAddress Ip { get; init; } = IPAddress.None;
        public string SessionId { get; init; } = string.Empty;
    }
}

/// <summary>应用对 prepare-upload 请求的决策，对应 Rust 端 <c>PrepareUploadDecisionV2</c>。</summary>
public abstract class PrepareUploadDecisionV2
{
    /// <summary>接受给定的文件 ID 子集；空集对应 204（无需传输）。</summary>
    public sealed class Accept : PrepareUploadDecisionV2
    {
        public HashSet<string> FileIds { get; }
        public Accept(HashSet<string> fileIds) { FileIds = fileIds; }
    }

    /// <summary>拒绝（返回 403）。</summary>
    public sealed class Decline : PrepareUploadDecisionV2 { }
}

/// <summary>会话结束原因，对应 Rust 端 <c>SessionEndReasonV2</c>。</summary>
public enum SessionEndReasonV2
{
    /// <summary>所有接受的文件到达终态。</summary>
    Finished,

    /// <summary>发送端通过 cancel 取消。</summary>
    Cancelled
}
