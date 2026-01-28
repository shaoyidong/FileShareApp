using FileShare.Core.Models;
using Microsoft.Data.Sqlite;
using System.IO;

namespace FileShare.Core.Services;

/// <summary>
/// 数据库服务实现
/// </summary>
public class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;
  
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="databasePath">数据库文件路径</param>
    public DatabaseService(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default
        }.ToString();
        InitializeDatabase(databasePath);
    }
    
    /// <summary>
    /// 初始化数据库
    /// </summary>
    private void InitializeDatabase(string databasePath)
    {
        // 确保目录存在
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        // 创建表
        using var connection = new SqliteConnection(_connectionString);
        {
            connection.Open();
            try
            {
                var createTableCmd = connection.CreateCommand();
                createTableCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS DeviceId (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceId TEXT NOT NULL UNIQUE,
                CreatedAt DATETIME NOT NULL
            );";
                createTableCmd.ExecuteNonQuery();
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
    
    /// <summary>
    /// 获取或创建设备ID
    /// </summary>
    /// <returns>设备ID</returns>
    public string GetOrCreateDeviceId()
    {
        using var connection = new SqliteConnection(_connectionString);
        {
            connection.Open();
            var selectCmd = connection.CreateCommand();
            selectCmd.CommandText = "SELECT * FROM DeviceId ORDER BY CreatedAt DESC LIMIT 1;";
            //var deviceId = new DeviceIdEntity()

            using var reader = selectCmd.ExecuteReader();
            {
                if (reader.Read())
                {
                    return reader.GetString(1);
                }
                else
                {
                    var newDeviceId = Guid.NewGuid().ToString();

                    var insertCmd = connection.CreateCommand();
                    insertCmd.CommandText = "INSERT INTO DeviceId (DeviceId, CreatedAt) VALUES (@deviceId, @createdAt);";
                    insertCmd.Parameters.AddWithValue("@deviceId", newDeviceId);
                    // 建议使用ISO8601格式存储日期时间
                    insertCmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    insertCmd.ExecuteNonQuery();

                    return newDeviceId;
                }
            }
        }
        
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
