namespace MonitoringXS.Platform.Windows.Caching;

public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _recency = new();
    private readonly object _gate = new();

    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity, comparer);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGetValue(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = default;
                return false;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                existing.Value = new Entry(key, value);
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                return;
            }

            LinkedListNode<Entry> node = _recency.AddFirst(new Entry(key, value));
            _entries.Add(key, node);
            if (_entries.Count <= _capacity)
            {
                return;
            }

            LinkedListNode<Entry> oldest = _recency.Last!;
            _recency.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }
    }

    private sealed record Entry(TKey Key, TValue Value);
}
