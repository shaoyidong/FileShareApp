using FileShare.Core.Models;
using System.ComponentModel.DataAnnotations;

namespace FileShare.Core.Models.Entities
{
    /// <summary>
    /// 传输历史（接收与发送共用一张表，用 <see cref="Direction"/> 区分）。
    /// </summary>
    public class ReceiveHistoryEntity
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 对端设备ID（接收时为发送方，发送时为接收方）
        /// </summary>
        public required string SenderId { get; set; }

        /// <summary>
        /// 对端设备名
        /// </summary>
        public string? SenderDeviceName { get; set; }

        /// <summary>
        /// 文件名（接收记录里是实际落盘的名字，可能带 " (1)" 后缀）
        /// </summary>
        public required string FileName { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public required long FileSize { get; set; }

        /// <summary>
        /// 文件所在目录（兼容旧记录；打开文件请优先用 <see cref="FilePath"/>）
        /// </summary>
        public required string SavePath { get; set; }

        /// <summary>
        /// 本机绝对文件路径：
        /// <para>接收记录 = 落盘位置；发送记录 = 源文件位置。</para>
        /// 为空表示该记录没有可打开的文件（例如发送文本后的临时文件、失败的接收）。
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// 传输结果（Completed / Failed / Cancelled）。
        /// </summary>
        public TransferStatus Status { get; set; }

        /// <summary>
        /// 失败/取消原因。
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 创建时间（UTC）
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 传输方向（Receive = 接收，Send = 发送）。
        /// </summary>
        public TransferDirection Direction { get; set; }

        /// <summary>对端设备 ID（新版字段，与 <see cref="SenderId"/> 等价，语义更清晰）。</summary>
        public string? PeerId { get; set; }

        /// <summary>对端设备名（新版字段）。</summary>
        public string? PeerName { get; set; }

        /// <summary>方向的中文描述（UI 绑定用，不入库）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DirectionText => Direction == TransferDirection.Send ? "发送" : "接收";

        /// <summary>结果的中文描述（UI 绑定用，不入库）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusText => Status switch
        {
            TransferStatus.Completed => "成功",
            TransferStatus.Failed => "失败",
            TransferStatus.Cancelled => "已取消",
            _ => "未知"
        };

        /// <summary>是否成功（UI 用于染色）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsSuccess => Status == TransferStatus.Completed;

        /// <summary>是否有可打开的文件。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasFile => !string.IsNullOrEmpty(FilePath);

        /// <summary>用于 UI 显示的对方名称。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string PeerDisplayName => PeerName ?? SenderDeviceName ?? PeerId ?? SenderId;
    }
}
