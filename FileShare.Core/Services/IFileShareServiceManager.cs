using System;
using FileShare.Core.Models;

namespace FileShare.Core.Services;

/// <summary>
/// 文件共享服务管理器接口
/// </summary>
public interface IFileShareServiceManager : IDisposable
{
    /// <summary>
    /// 设备列表更新事件
    /// </summary>
    event Action<DeviceInfo>? OnDeviceDiscovered;
    
    /// <summary>
    /// 文件传输请求事件
    /// </summary>
    event Action<FileTransferInfo>? OnTransferRequestSendAndReceive;
    
    /// <summary>
    /// 传输进度更新事件
    /// </summary>
    event Action<FileTransferInfo>? OnTransferProgressUpdated;
    
    /// <summary>
    /// 传输完成事件
    /// </summary>
    event Action<FileTransferInfo, string?>? OnTransferCompleted;
    
    /// <summary>
    /// 启动服务
    /// </summary>
    Task StartServicesAsync();
    
    /// <summary>
    /// 停止服务
    /// </summary>
    Task StopServicesAsync();
    
    /// <summary>
    /// 获取本地设备信息
    /// </summary>
    DeviceInfo GetLocalDeviceInfo();
    
    /// <summary>
    /// 发送文件到目标设备
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="targetDevice">目标设备</param>
    /// <returns>是否发送成功</returns>
    Task<bool> SendFileAsync(string filePath, DeviceInfo targetDevice);
    
    /// <summary>
    /// 手动刷新设备列表
    /// </summary>
    void RefreshDevices();
    
    /// <summary>
    /// 处理文件传输请求
    /// </summary>
    /// <param name="transferId">传输ID</param>
    /// <param name="accept">是否接受</param>
    /// <param name="savePath">文件保存路径</param>
    void HandleTransferRequest(string transferId, bool accept, string? savePath = null);
    
    /// <summary>
    /// 取消传输
    /// </summary>
    /// <param name="transferId">传输ID</param>
    void CancelTransfer(string transferId);
}