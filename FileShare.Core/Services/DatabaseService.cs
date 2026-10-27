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
                FileSize INTEGER NOT NULL,
                SavePath TEXT NOT NULL,
                CreatedAt DATETIME NOT NULL,
                Direction INTEGER NOT NULL DEFAULT 0,
                PeerId TEXT,
                PeerName TEXT,
                FilePath TEXT,
                Status INTEGER NOT NULL DEFAULT 2,
                ErrorMessage TEXT
            );";
        
        connection.Execute(createTableSql);

        // 老版本数据库没有 Direction / PeerId / PeerName 列，这里做一次幂等迁移
        EnsureColumn(connection, "ReceiveHistory", "Direction", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "ReceiveHistory", "PeerId", "TEXT");
        EnsureColumn(connection, "ReceiveHistory", "PeerName", "TEXT");
        EnsureColumn(connection, "ReceiveHistory", "FilePath", "TEXT");
        EnsureColumn(connection, "ReceiveHistory", "Status", "INTEGER NOT NULL DEFAULT 2");
        EnsureColumn(connection, "ReceiveHistory", "ErrorMessage", "TEXT");
    }

    /// <summary>
    /// 如果表缺少某列则补上（SQLite 不支持 ADD COLUMN IF NOT EXISTS，用 PRAGMA 判断）。
    /// </summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        try
        {
            var columns = connection.Query<TableColumnInfo>($"PRAGMA table_info({table});");
            foreach (var info in columns)
            {
                if (string.Equals(info.Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            connection.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }
        catch (Exception)
        {
            // 迁移失败不应阻止应用启动：旧库仍可读写（只是缺少新字段）
        }
    }

    /// <summary>PRAGMA table_info 的结果行。</summary>
    private sealed class TableColumnInfo
    {
        public string Name { get; set; } = string.Empty;
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
    /// 增加一条传输历史（接收/发送共用）
    /// </summary>
    /// <param name="receiveHistory">历史记录</param>
    /// <returns>是否写入成功</returns>
    public async Task<bool> AddSingleReceiveHistoryAsync(ReceiveHistoryEntity receiveHistory)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        
        const string insertSql = @"INSERT INTO ReceiveHistory (SenderId, SenderDeviceName, FileName, FileSize, SavePath, CreatedAt, Direction, PeerId, PeerName, FilePath, Status, ErrorMessage)
                                   VALUES (@SenderId, @SenderDeviceName, @FileName, @FileSize, @SavePath, @CreatedAt, @Direction, @PeerId, @PeerName, @FilePath, @Status, @ErrorMessage);";
        receiveHistory.PeerId ??= receiveHistory.SenderId;
        receiveHistory.PeerName ??= receiveHistory.SenderDeviceName;
        var result = await connection.ExecuteAsync(insertSql, receiveHistory);
        
        return result > 0;
    }
    
    /// <summary>
    /// 删除一条历史
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
    /// 清空历史
    /// </summary>
    /// <returns></returns>
    public async Task<bool> ClearReceiveHistoryAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // SQLite 不支持 TRUNCATE，使用 DELETE 清空表
        const string deleteSql = @"DELETE FROM ReceiveHistory;
                                   DELETE FROM sqlite_sequence WHERE name = 'ReceiveHistory';";
        await connection.ExecuteAsync(deleteSql);
        return true;
    }
    
    /// <summary>
    /// 获取所有历史（接收 + 发送），按时间倒序
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
