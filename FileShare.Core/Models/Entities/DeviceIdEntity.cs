using System.ComponentModel.DataAnnotations;

namespace FileShare.Core.Models.Entities;

/// <summary>
/// 设备ID实体类
/// </summary>
public class DeviceIdEntity
{
    /// <summary>
    /// 主键ID
    /// </summary>
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// 设备ID
    /// </summary>
    public string DeviceId { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
