using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MonitoringXS.Application;

public interface IMonitoringSnapshotSource
{
    MonitoringSnapshot? Latest { get; }

    IAsyncEnumerable<MonitoringSnapshot> SubscribeAsync(CancellationToken cancellationToken = default);
}

public sealed class MonitoringSnapshotHub : IMonitoringSnapshotSource
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Channel<MonitoringSnapshot>> _subscribers = [];
    private MonitoringSnapshot? _latest;
    private long _nextSubscriberId;

    public MonitoringSnapshot? Latest => Volatile.Read(ref _latest);

    public void Publish(MonitoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
        lock (_gate)
        {
            foreach (Channel<MonitoringSnapshot> subscriber in _subscribers.Values)
            {
                subscriber.Writer.TryWrite(snapshot);
            }
        }
    }

    public async IAsyncEnumerable<MonitoringSnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<MonitoringSnapshot> channel = Channel.CreateBounded<MonitoringSnapshot>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        long id;
        MonitoringSnapshot? latest;
        lock (_gate)
        {
            id = ++_nextSubscriberId;
            _subscribers.Add(id, channel);
            latest = _latest;
        }

        if (latest is not null)
        {
            channel.Writer.TryWrite(latest);
        }

        try
        {
            await foreach (MonitoringSnapshot snapshot in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return snapshot;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(id);
            }

            channel.Writer.TryComplete();
        }
    }
}
