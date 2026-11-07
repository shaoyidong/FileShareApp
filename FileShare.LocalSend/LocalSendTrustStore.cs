using System.Text.Json;

namespace FileShare.LocalSend;

/// <summary>
/// LocalSend 对端证书指纹的 TOFU 信任库（按 <c>ip:port</c> 记录）。
/// <para>持久化走源生成 JSON 元数据，AOT 下不触发反射序列化。</para>
/// </summary>
public sealed class LocalSendTrustStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pins;

    private LocalSendTrustStore(string filePath, Dictionary<string, string> pins)
    {
        _filePath = filePath;
        _pins = pins;
    }

    /// <summary>读取或创建信任库。</summary>
    public static LocalSendTrustStore LoadOrCreate(string filePath)
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(filePath))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(filePath), LocalSendIdentityJsonContext.Default.StringMap);
                if (loaded is not null)
                {
                    foreach (var pair in loaded)
                    {
                        pins[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 损坏就当空库
        }

        return new LocalSendTrustStore(filePath, pins);
    }

    /// <summary>
    /// 用 TOFU 规则校验对端指纹。
    /// </summary>
    /// <param name="endpoint">对端 <c>ip:port</c>（信任库的 key）。</param>
    /// <param name="fingerprint">本次握手中实际看到的证书指纹。</param>
    /// <returns>是否可信、是否首次记录。</returns>
    public (bool Trusted, bool FirstSeen) Verify(string endpoint, string fingerprint)
    {
        lock (_gate)
        {
            if (_pins.TryGetValue(endpoint, out var known))
            {
                return (string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase), false);
            }

            _pins[endpoint] = fingerprint;
            Save();
            return (true, true);
        }
    }

    /// <summary>当前已信任的对端数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pins.Count;
            }
        }
    }

    /// <summary>清除全部信任记录。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pins.Clear();
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(
                _pins, LocalSendIdentityJsonContext.Default.StringMap));
        }
        catch (Exception)
        {
            // 持久化失败不影响本次运行
        }
    }
}
