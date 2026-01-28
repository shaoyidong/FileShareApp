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
}
