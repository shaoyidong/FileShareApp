using System.Collections;

namespace LocalSend.Core.Util;

/// <summary>
/// 简单的 LRU（最近最少使用）缓存，对应 Rust 端使用的 <c>lru::LruCache</c>。
/// </summary>
/// <remarks>
/// 不是线程安全的；调用方需要自行加锁（参见 <c>System.Threading.Mutex</c> /
/// <c>SemaphoreSlim</c>），与 Rust 端 <c>Arc&lt;Mutex&lt;LruCache&gt;&gt;</c> 用法一致。
/// </remarks>
public sealed class LruCache<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _lookup = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }
        _capacity = capacity;
    }

    /// <summary>当前缓存项数。</summary>
    public int Count => _lookup.Count;

    /// <summary>
    /// 插入或更新条目；超出容量时淘汰最近最少使用的项。
    /// 对应 Rust 的 <c>LruCache::put</c>。
    /// </summary>
    public void Put(TKey key, TValue value)
    {
        if (_lookup.TryGetValue(key, out var node))
        {
            // 更新值并提升到队首（最新使用）。
            node.Value = new KeyValuePair<TKey, TValue>(key, value);
            _order.Remove(node);
            _order.AddFirst(node);
        }
        else
        {
            if (_lookup.Count >= _capacity)
            {
                // 淘汰队尾（最久未使用）。
                var lru = _order.Last!;
                _order.RemoveLast();
                _lookup.Remove(lru.Value.Key);
            }
            var newNode = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
            _lookup[key] = newNode;
        }
    }

    /// <summary>
    /// 查询键值，命中时提升到队首。未命中返回 default。
    /// 对应 Rust 的 <c>LruCache::get</c>。
    /// </summary>
    public TValue? Get(TKey key)
    {
        if (!_lookup.TryGetValue(key, out var node))
        {
            return default;
        }
        _order.Remove(node);
        _order.AddFirst(node);
        return node.Value.Value;
    }

    /// <summary>查询键值（命中不提升），对应 Rust 的 <c>LruCache::peek</c>。</summary>
    public TValue? Peek(TKey key)
    {
        return _lookup.TryGetValue(key, out var node) ? node.Value.Value : default;
    }

    /// <summary>移除指定键，对应 Rust 的 <c>LruCache::pop</c>。</summary>
    public bool Remove(TKey key)
    {
        if (!_lookup.TryGetValue(key, out var node))
        {
            return false;
        }
        _order.Remove(node);
        _lookup.Remove(key);
        return true;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _order.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
