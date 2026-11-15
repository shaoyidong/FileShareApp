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

    /// <summary>
    /// 分页获取历史（键集分页，按 Id 倒序 = 新的在前）。
    /// 历史记录可能积累成千上万条，列表页应增量加载而不是一次取全表。
    /// </summary>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="beforeId">游标：只取 Id 小于该值的记录；null 表示从最新开始。</param>
    /// <param name="direction">方向筛选；null 表示全部。</param>
    Task<IReadOnlyList<ReceiveHistoryEntity>> GetReceiveHistoryPageAsync(
        int pageSize, int? beforeId, FileShare.Core.Models.TransferDirection? direction);

    /// <summary>
    /// 统计历史总条数（可按方向筛选），供列表页显示「共 N 条」。
    /// </summary>
    Task<int> CountReceiveHistoryAsync(FileShare.Core.Models.TransferDirection? direction);
}
