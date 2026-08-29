using MonitoringXS.Application;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Application.Tests;

public sealed class MonitoringSnapshotHubTests
{
    [Fact]
    public void PublishUpdatesLatestWithoutSubscriber()
    {
        MonitoringSnapshotHub hub = new();
        MonitoringSnapshot snapshot = Snapshot(1);

        hub.Publish(snapshot);

        Assert.Same(snapshot, hub.Latest);
    }

    [Fact]
    public async Task SlowSubscriberReceivesLatestAndDoesNotBlockPublisher()
    {
        MonitoringSnapshotHub hub = new();
        await using IAsyncEnumerator<MonitoringSnapshot> subscriber = hub
            .SubscribeAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        MonitoringSnapshot latest = Snapshot(3);

        hub.Publish(Snapshot(1));
        hub.Publish(Snapshot(2));
        hub.Publish(latest);

        Assert.True(await subscriber.MoveNextAsync());
        Assert.Same(latest, subscriber.Current);
    }

    [Fact]
    public async Task MultipleSubscribersAreIndependent()
    {
        MonitoringSnapshotHub hub = new();
        await using IAsyncEnumerator<MonitoringSnapshot> first = hub
            .SubscribeAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<MonitoringSnapshot> second = hub
            .SubscribeAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        MonitoringSnapshot snapshot = Snapshot(1);

        hub.Publish(snapshot);

        Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken));
        Assert.True(await second.MoveNextAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken));
        Assert.Same(snapshot, first.Current);
        Assert.Same(snapshot, second.Current);
    }

    [Fact]
    public async Task SubscriberCancellationEndsEnumeration()
    {
        MonitoringSnapshotHub hub = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
#pragma warning disable xUnit1051 // Linked token is intentionally canceled by this test.
        await using IAsyncEnumerator<MonitoringSnapshot> subscriber = hub
            .SubscribeAsync(cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
#pragma warning restore xUnit1051
        Task<bool> pending = subscriber.MoveNextAsync().AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        hub.Publish(Snapshot(1));
        Assert.NotNull(hub.Latest);
    }

    private static MonitoringSnapshot Snapshot(int sequence) => new(
        DateTimeOffset.UtcNow.AddTicks(sequence),
        new ProcessDiscoverySnapshot([], [], []),
        [],
        new Dictionary<string, IReadOnlyList<ApplicationHistoryPoint>>(StringComparer.Ordinal));
}
