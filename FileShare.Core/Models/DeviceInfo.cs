namespace FileShare.Core.Models;

/// <summary>
/// 表示局域网中的设备信息
/// </summary>
public class DeviceInfo
{
    /// <summary>
    /// 设备唯一标识符
    /// </summary>
    public required string DeviceId { get; set; }
    
    /// <summary>
    /// 设备名称
    /// </summary>
    public required string DeviceName { get; set; }
    
    /// <summary>
    /// 设备IP地址
    /// </summary>
    public required string IpAddress { get; set; }
    
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

    /// <summary>
    /// 是否支持 TLS 加密传输。通过发现协议广播，发送方据此决定是否升级到 SslStream。
    /// </summary>
    public bool SupportsTls { get; set; }
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