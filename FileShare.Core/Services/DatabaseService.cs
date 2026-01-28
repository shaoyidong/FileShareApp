using FileShare.Core.Models;
using Microsoft.Data.Sqlite;
using System.Data;
using System.IO;
using Dapper;

namespace FileShare.Core.Services;

/// <summary>
/// 数据库服务实现
/// </summary>
[DapperAot]
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
        connection.Open();
        
        const string createTableSql = @"
            CREATE TABLE IF NOT EXISTS DeviceId (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceId TEXT NOT NULL UNIQUE,
                CreatedAt DATETIME NOT NULL
            );";
        
        connection.Execute(createTableSql);
    }
    
    /// <summary>
    /// 获取或创建设备ID
    /// </summary>
    /// <returns>设备ID</returns>
    public string GetOrCreateDeviceId()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        const string selectSql = "SELECT * FROM DeviceId ORDER BY CreatedAt DESC LIMIT 1;";
        var deviceIdEntity = connection.QueryFirstOrDefault<DeviceIdEntity>(selectSql);
        
        if (deviceIdEntity != null)
        {
            return deviceIdEntity.DeviceId;
        }
        
        // 创建新设备ID
        var newDeviceId = Guid.NewGuid().ToString();
        var newEntity = new DeviceIdEntity
        {
            DeviceId = newDeviceId,
            CreatedAt = DateTime.UtcNow
        };
        
        const string insertSql = "INSERT INTO DeviceId (DeviceId, CreatedAt) VALUES (@DeviceId, @CreatedAt);";
        connection.Execute(insertSql, newEntity);
        
        return newDeviceId;
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
