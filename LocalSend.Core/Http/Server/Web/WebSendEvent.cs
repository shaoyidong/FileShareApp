using System.Net;
using System.Text.Json.Serialization;
using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Server.Web;

/// <summary>
/// Web 发送（Download API）相关事件，对应 Rust 端 <c>src/http/server/web.rs</c> 中的 <c>WebSendEvent</c>。
/// </summary>
public abstract class WebSendEvent
{
    /// <summary>
    /// Web 客户端通过 <c>POST /api/localsend/v2/prepare-download</c> 请求下载共享文件。
    /// 应用必须通过 <see cref="DecisionTx"/> 返回是否接受。
    /// </summary>
    public sealed class PrepareDownload : WebSendEvent
    {
        public IPAddress Ip { get; init; } = IPAddress.None;
        public string SessionId { get; init; } = string.Empty;
        public string? UserAgent { get; init; }
        public TaskCompletionSource<bool> DecisionTx { get; init; } = new();
    }

    /// <summary>
    /// 已接受的 Web 客户端通过 <c>GET /api/localsend/v2/download</c> 下载文件。
    /// 应用必须通过 <see cref="ContentTx"/> 提供文件内容流。
    /// </summary>
    public sealed class FileDownload : WebSendEvent
    {
        public string SessionId { get; init; } = string.Empty;
        public string FileId { get; init; } = string.Empty;
        public FileDto File { get; init; } = new();
        public TaskCompletionSource<FileContent> ContentTx { get; init; } = new();
    }
}

/// <summary>
/// Web 页面 i18n 文案，对应 Rust 端 <c>WebSendI18n</c>。
/// </summary>
public class WebSendI18n
{
    [JsonPropertyName("waiting")] public string Waiting { get; set; } = "Waiting for response…";
    [JsonPropertyName("enterPin")] public string EnterPin { get; set; } = "Enter PIN";
    [JsonPropertyName("invalidPin")] public string InvalidPin { get; set; } = "Invalid PIN";
    [JsonPropertyName("tooManyAttempts")] public string TooManyAttempts { get; set; } = "Too many attempts";
    [JsonPropertyName("rejected")] public string Rejected { get; set; } = "Rejected";
    [JsonPropertyName("files")] public string Files { get; set; } = "Files";
    [JsonPropertyName("fileName")] public string FileName { get; set; } = "File name";
    [JsonPropertyName("size")] public string Size { get; set; } = "Size";
}
