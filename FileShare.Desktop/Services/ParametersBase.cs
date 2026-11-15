using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Services;

/// <summary>
/// 键值参数集合基类：弹窗参数（DialogParameters）与导航参数（NavigationParameters）共用。
/// <para>读取为直接强转，不做反射类型转换，保持 NativeAOT 兼容。</para>
/// </summary>
public abstract class ParametersBase
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>添加或覆盖参数的索引器，支持集合初始化器写法：new XParameters { ["key"] = value }。</summary>
    public object? this[string key]
    {
        set => _values[key] = value;
        get => _values.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>添加或覆盖一个参数。</summary>
    public void Add<T>(string key, T? value) => _values[key] = value;

    /// <summary>读取参数；键不存在或类型不匹配时抛出异常。</summary>
    public T GetValue<T>(string key)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            throw new KeyNotFoundException($"Parameter '{key}' not found.");
        }

        return GetValueCore<T>(key, raw);
    }

    /// <summary>尝试读取参数。</summary>
    public bool TryGetValue<T>(string key, out T value)
    {
        if (_values.TryGetValue(key, out var raw))
        {
            value = GetValueCore<T>(key, raw);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>是否包含指定键。</summary>
    public bool ContainsKey(string key) => _values.ContainsKey(key);

    private static T GetValueCore<T>(string key, object? raw)
    {
        if (raw is null)
        {
            return default!;
        }

        if (raw is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            $"Parameter '{key}' is {raw.GetType().Name}, expected {typeof(T).Name}.");
    }
}
