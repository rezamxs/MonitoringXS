namespace MonitoringXS.Core.Collections;

public sealed class BoundedTimeSeries<T>
{
    private readonly Queue<(DateTimeOffset Timestamp, T Value)> _items;

    public BoundedTimeSeries(int capacity)
        : this(capacity, null)
    {
    }

    /// <summary>
    /// Keeps at most <paramref name="capacity"/> items and, when a window is given,
    /// only items newer than the latest timestamp minus that window. A time window
    /// keeps "last minute" charts a minute long at every sampling interval, so
    /// slower sampling also stores and draws fewer points.
    /// </summary>
    public BoundedTimeSeries(int capacity, TimeSpan? window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (window is { } span)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero, nameof(window));
        }

        Capacity = capacity;
        Window = window;
        _items = new Queue<(DateTimeOffset Timestamp, T Value)>(capacity);
    }

    public int Capacity { get; }

    public TimeSpan? Window { get; }

    public int Count => _items.Count;

    public void Add(DateTimeOffset timestamp, T value)
    {
        if (_items.Count == Capacity)
        {
            _items.Dequeue();
        }

        _items.Enqueue((timestamp, value));
        if (Window is { } window)
        {
            DateTimeOffset oldestKept = timestamp - window;
            while (_items.Count > 1 && _items.Peek().Timestamp <= oldestKept)
            {
                _items.Dequeue();
            }
        }
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
