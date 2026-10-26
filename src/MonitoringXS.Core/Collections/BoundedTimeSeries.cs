namespace MonitoringXS.Core.Collections;

public sealed class BoundedTimeSeries<T>
{
    private readonly Queue<(DateTimeOffset Timestamp, T Value)> _items;

    public BoundedTimeSeries(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
        _items = new Queue<(DateTimeOffset Timestamp, T Value)>(capacity);
    }

    public int Capacity { get; }

    public int Count => _items.Count;

    public void Add(DateTimeOffset timestamp, T value)
    {
        if (_items.Count == Capacity)
        {
            _items.Dequeue();
        }

        _items.Enqueue((timestamp, value));
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, T Value)> Snapshot() => _items.ToArray();

    // Single-allocation values snapshot: avoids the tuple array + Select copy
    // that the per-tick history paths used to pay for every series.
    public IReadOnlyList<T> Values()
    {
        T[] values = new T[_items.Count];
        int index = 0;
        foreach ((DateTimeOffset _, T value) in _items)
        {
            values[index++] = value;
        }

        return values;
    }
}
