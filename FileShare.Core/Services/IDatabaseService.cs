using FileShare.Core.Models.Entities;

namespace FileShare.Core.Services;

/// <summary>
/// 数据库服务接口
/// </summary>
public interface IDatabaseService
{
    /// <summary>
    /// 获取或创建设备ID
    /// </summary>
    /// <returns>设备ID</returns>
    string GetOrCreateDeviceId();

    /// <summary>
    /// 增加一条接收历史
    /// </summary>
    /// <param name="receiveHistory"></param>
    /// <returns></returns>
    Task<bool> AddSingleReceiveHistoryAsync(ReceiveHistoryEntity receiveHistory);

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
