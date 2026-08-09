using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using System;

namespace FileShare.Core.Services;

/// <summary>
/// 文件共享服务管理器接口
/// </summary>
public interface IFileShareServiceManager : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 设备列表更新事件
    /// </summary>
    event Action<DeviceInfo>? OnDeviceDiscovered;
    
    /// <summary>
    /// 设备离线事件
    /// </summary>
    event Action<DeviceInfo>? OnDeviceRemoved;
    
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
    Task RefreshDevicesAsync();
    
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

    /// <summary>
    /// 删除一条接收历史
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    Task<bool> DeleteSingleReceiveHistoryAsync(int id);

    /// <summary>
    /// 清空接收历史
    /// </summary>
    /// <returns></returns>
    Task<bool> ClearReceiveHistoryAsync();

    /// <summary>
    /// 获取所有接收历史
    /// </summary>
    /// <returns></returns>
    Task<IEnumerable<ReceiveHistoryEntity>> GetAllReceiveHistoryAsync();
}