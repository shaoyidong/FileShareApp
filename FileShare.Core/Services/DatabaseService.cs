using FileShare.Core.Models;
using FileShare.Core.Models.Entities;
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
            );
            
            CREATE TABLE IF NOT EXISTS ReceiveHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SenderId TEXT NOT NULL,
                SenderDeviceName TEXT,
                FileName TEXT NOT NULL,
                SavePath TEXT NOT NULL,
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
    /// 增加一条接收历史
    /// </summary>
    /// <param name="receiveHistory"></param>
    /// <returns></returns>
    public async Task<bool> AddSingleReceiveHistoryAsync(ReceiveHistoryEntity receiveHistory)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        const string insertSql = "INSERT INTO ReceiveHistory (SenderId, SenderDeviceName, FileName, SavePath, CreatedAt) VALUES (@SenderId, @SenderDeviceName, @FileName, @SavePath, @CreatedAt);";
        var result = await connection.ExecuteAsync(insertSql, receiveHistory);
        
        return result > 0;
    }
    
    /// <summary>
    /// 删除一条接收历史
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public async Task<bool> DeleteSingleReceiveHistoryAsync(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        const string deleteSql = "DELETE FROM ReceiveHistory WHERE Id = @Id;";
        var result = await connection.ExecuteAsync(deleteSql, new { Id = id });
        
        return result > 0;
    }
    
    /// <summary>
    /// 清空接收历史
    /// </summary>
    /// <returns></returns>
    public async Task<bool> ClearReceiveHistoryAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        const string deleteSql = "TRUNCATE TABLE ReceiveHistory;";
        await connection.ExecuteAsync(deleteSql);
        
        return true;
    }
    
    /// <summary>
    /// 获取所有接收历史
    /// </summary>
    /// <returns></returns>
    public async Task<IEnumerable<ReceiveHistoryEntity>> GetAllReceiveHistoryAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        const string selectSql = "SELECT * FROM ReceiveHistory ORDER BY CreatedAt DESC;";
        var result = await connection.QueryAsync<ReceiveHistoryEntity>(selectSql);
        
        return result;
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
