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
}
