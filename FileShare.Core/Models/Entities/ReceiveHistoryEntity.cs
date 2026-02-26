using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace FileShare.Core.Models.Entities
{
    public class ReceiveHistoryEntity
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 发送方ID
        /// </summary>
        public required string SenderId { get; set; }

        /// <summary>
        /// 发送方设备名
        /// </summary>
        public string? SenderDeviceName { get; set; }

        /// <summary>
        /// 接收文件名
        /// </summary>
        public required string FileName { get; set; }

        /// <summary>
        /// 保存目录
        /// </summary>
        public required string SavePath { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }
}
