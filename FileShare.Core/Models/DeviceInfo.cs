namespace FileShare.Core.Models;

/// <summary>
/// 表示局域网中的设备信息
/// </summary>
public class DeviceInfo
{
    /// <summary>
    /// 设备唯一标识符
    /// </summary>
    public string DeviceId { get; set; }
    
    /// <summary>
    /// 设备名称
    /// </summary>
    public string DeviceName { get; set; }
    
    /// <summary>
    /// 设备IP地址
    /// </summary>
    public string IpAddress { get; set; }
    
    /// <summary>
    /// 服务端口
    /// </summary>
    public int Port { get; set; }
    
    /// <summary>
    /// 设备类型
    /// </summary>
    public DeviceType DeviceType { get; set; }
    
    /// <summary>
    /// 最后在线时间
    /// </summary>
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// 设备类型枚举
/// </summary>
public enum DeviceType
{
    Desktop,
    Mobile,
    Tablet
}