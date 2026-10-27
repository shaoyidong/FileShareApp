using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
using FileShare.Core.Network.Discovery;
using System;
using System.Collections.Generic;
using System.Threading;

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
    event Action<string>? OnDeviceRemoved;
    
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
    /// 首次信任某设备证书（TOFU）时触发：(设备ID, 指纹)。
    /// </summary>
    event Action<string, string>? OnFirstUseTrusted;

    /// <summary>
    /// 证书指纹与记录不一致时触发：(设备ID, 已知指纹, 本次指纹)。
    /// </summary>
    event Action<string, string, string>? OnFingerprintMismatch;
    
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
    /// 一次发送多个文件（顺序发送）。
    /// </summary>
    /// <param name="filePaths">文件路径列表。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="progress">批次进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> SendFilesAsync(
        IReadOnlyList<string> filePaths,
        DeviceInfo targetDevice,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int parallelism = 1,
        TransferQueueControl? queue = null);

    /// <summary>
    /// 发送整个文件夹（递归，保留目录结构）。
    /// </summary>
    /// <param name="folderPath">本地文件夹。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="progress">批次进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="parallelism">并发数量（1~4）。</param>
    Task<bool> SendFolderAsync(
        string folderPath,
        DeviceInfo targetDevice,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int parallelism = 1,
        TransferQueueControl? queue = null);

    /// <summary>
    /// 发送文本（对端收到一个 .txt 文件）。
    /// </summary>
    /// <param name="text">文本内容。</param>
    /// <param name="targetDevice">目标设备。</param>
    /// <param name="fileName">对端看到的文件名。</param>
    /// <param name="progress">进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> SendTextAsync(
        string text,
        DeviceInfo targetDevice,
        string? fileName = null,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送单个文件（队列逐项发送时使用）。
    /// </summary>
    Task<bool> SendSingleAsync(
        string filePath,
        DeviceInfo targetDevice,
        string? relativePath = null,
        TransferQueueControl? queue = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取已信任的设备证书指纹。
    /// </summary>
    IReadOnlyList<(string DeviceId, string Fingerprint, DateTime NotAfterUtc)> GetTrustedDevices();

    /// <summary>
    /// 移除某个设备的信任记录。
    /// </summary>
    bool ForgetTrustedDevice(string deviceId);

    /// <summary>
    /// 清空所有信任记录。
    /// </summary>
    void ClearTrustedDevices();

    /// <summary>
    /// 直接按 IP / 主机名连接设备（跨子网、广播被拦截时的兜底方案）。
    /// </summary>
    /// <param name="address">地址，支持 ip、ip:port、[ipv6]:port、主机名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<DeviceInfo?> ConnectByAddressAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描网段并把发现的设备合并进设备列表。
    /// </summary>
    /// <param name="cidr">网段，如 192.168.1.0/24。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<DeviceInfo>> ScanSubnetAsync(
        string cidr,
        IProgress<SubnetScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    
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
