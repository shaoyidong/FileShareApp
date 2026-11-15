using System;
using System.Collections.Generic;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 弹窗参数键值集合（仿 Prism 的 IDialogParameters）。
/// <para>读取为直接强转，不做反射类型转换，保持 NativeAOT 兼容。</para>
/// </summary>
public interface IDialogParameters
{
    /// <summary>添加或覆盖一个参数。</summary>
    void Add<T>(string key, T? value);

    /// <summary>读取参数；键不存在或类型不匹配时抛出异常。</summary>
    T GetValue<T>(string key);

    /// <summary>尝试读取参数。</summary>
    bool TryGetValue<T>(string key, out T value);

    /// <summary>是否包含指定键。</summary>
    bool ContainsKey(string key);
}

/// <inheritdoc />
public sealed class DialogParameters : IDialogParameters
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>创建空参数集合。</summary>
    public DialogParameters()
    {
    }

    /// <summary>添加或覆盖参数的索引器，支持集合初始化器写法：new DialogParameters { ["key"] = value }。</summary>
    public object? this[string key]
    {
        set => _values[key] = value;
        get => _values.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc />
    public void Add<T>(string key, T? value) => _values[key] = value;

    /// <inheritdoc />
    public T GetValue<T>(string key)
    {
        if (!_values.TryGetValue(key, out var raw))
        {
            throw new KeyNotFoundException($"Dialog parameter '{key}' not found.");
        }

        return GetValueCore<T>(key, raw);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            $"Dialog parameter '{key}' is {raw.GetType().Name}, expected {typeof(T).Name}.");
    }
}
