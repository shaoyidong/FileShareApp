namespace FileShare.Core.Network;

/// <summary>
/// 传输请求类型
/// </summary>
public enum TransferRequestType
{
    SendFileRequest,
    SendFileData,
    FileChecksum,

    /// <summary>
    /// 原生文本消息：一个请求直接携带全文，免确认、不落盘。
    /// 追加在末尾以保持旧枚举数值（0/1/2）不变，避免破坏旧客户端的文件传输。
    /// </summary>
    SendTextMessage,
}

/// <summary>
/// 传输请求
/// </summary>
public class TransferRequest
{
    public TransferRequestType Type { get; set; }
    public required string TransferId { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public required string SenderId { get; set; }
    public required string ReceiverId { get; set; }
    public string? Checksum { get; set; }

    /// <summary>
    /// 文件夹传输时的相对路径（例如 <c>相册/2024/IMG_0001.jpg</c>）。
    /// 单文件传输为 <c>null</c>，旧客户端不认识该字段时会忽略，退化为平铺保存。
    /// </summary>
    public string? RelativePath { get; set; }

    /// <summary>断点续传起点（字节，0 表示从头）。</summary>
    public long StartOffset { get; set; }

    /// <summary>
    /// 原生文本消息内容（仅 <see cref="TransferRequestType.SendTextMessage"/> 使用）。
    /// 旧客户端不认识该字段时会忽略，不会破坏既有文件传输。
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// 传输响应
/// </summary>
public class TransferResponse
{
    public required string TransferId { get; set; }
    public bool Accepted { get; set; }
    public string? Message { get; set; }

    /// <summary>接收端已有字节数，发送端应从这里继续；0 表示从头。</summary>
    public long ResumeOffset { get; set; }
}
