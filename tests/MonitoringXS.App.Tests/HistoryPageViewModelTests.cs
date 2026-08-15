using MonitoringXS.App.Controls;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class HistoryPageViewModelTests
{
    [Fact]
    public void PresentationPreservesPartialAndLifetimeGapsWithoutInventingZero()
    {
        DateTimeOffset start = new(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);
        HistoryMetricDefinition definition = new(
            MetricHistoryMetric.CpuPercent,
            "CPU",
            HistoryValueKind.Percent);
        MetricHistoryQueryResult result = new(
        [
            new("app", "pid-1/1", start, MetricHistoryMetric.CpuPercent, 12, MetricAvailability.Available, null, false),
            new("app", "pid-1/1", start.AddSeconds(1), MetricHistoryMetric.CpuPercent, null, MetricAvailability.Partial, "lower bound", false),
            new("app", "pid-2/2", start.AddSeconds(2), MetricHistoryMetric.CpuPercent, 30, MetricAvailability.Available, null, false)
        ],
        true);

        var presentation = HistorySeriesPresentation.Create(
            definition,
            result,
            new HistoryRangeOption("15 minutes", TimeSpan.FromMinutes(15)),
            20);

        Assert.Equal(4, presentation.Samples.Count);
        Assert.Equal(12, presentation.Samples[0].Value);
        Assert.Null(presentation.Samples[1].Value);
        Assert.Null(presentation.Samples[2].Value);
        Assert.Equal(30, presentation.Samples[3].Value);
        Assert.Equal("Partial", presentation.State);
        Assert.Contains("lower-bound", presentation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationReportsUnavailableInsteadOfZero()
    {
        HistoryMetricDefinition definition = new(
            MetricHistoryMetric.NetworkDownloadBytesPerSecond,
            "Network receive",
            HistoryValueKind.BytesPerSecond);
        MetricHistoryQueryResult result = new(
        [
            new("app", "lifetime", DateTimeOffset.UtcNow, definition.Metric, 0, MetricAvailability.Unavailable, null, false)
        ],
        true);

        var presentation = HistorySeriesPresentation.Create(
            definition,
            result,
            new HistoryRangeOption("1 hour", TimeSpan.FromHours(1)),
            20);

        Assert.Equal("Unavailable", presentation.State);
        Assert.Null(presentation.Samples.Single().Value);
        Assert.Contains("no measured values", presentation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecimatorBoundsLargeRangesAndKeepsEndpointsAndExtrema()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        CpuHistorySample[] samples = Enumerable.Range(0, 100)
            .Select(index => new CpuHistorySample(
                start.AddSeconds(index),
                index == 42 ? 999 : index))
            .ToArray();

        IList<CpuHistorySample> display = HistoryPointDecimator.Decimate(samples, 12);

        Assert.Equal(12, display.Count);
        Assert.Equal(samples[0], display[0]);
        Assert.Equal(samples[^1], display[^1]);
        Assert.Contains(display, sample => sample.Value == 999);
    }

    [Fact]
    public async Task ViewModelLoadsApplicationsAndAllMetricCharts()
    {
        DateTimeOffset now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
        FakeHistoryStore store = new(
            [
                new("app-1", "Demo", ApplicationDisposition.Installed, now)
            ],
            metric => new MetricHistoryQueryResult(
            [
                new("app-1", "lifetime", now.AddMinutes(-1), metric, 5, MetricAvailability.Available, null, false)
            ],
            true));
        using HistoryPageViewModel viewModel = new(
            store,
            () => now,
            TimeSpan.Zero,
            20);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(HistoryPageState.Ready, viewModel.State);
        Assert.Single(viewModel.Applications);
        Assert.All(viewModel.Charts, chart => Assert.Single(chart.Samples));
        Assert.Equal(11, store.QueriedMetrics.Count);
        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24)],
            viewModel.Ranges.Select(range => range.Duration));
        Assert.Equal(TimeSpan.FromHours(1), viewModel.SelectedRange.Duration);
        Assert.Contains("History loaded", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewModelSurfacesDatabaseUnavailable()
    {
        FakeHistoryStore store = new(
            [new("app", "App", ApplicationDisposition.Installed, DateTimeOffset.UtcNow)],
            _ => new MetricHistoryQueryResult([], false, "locked"));
        using HistoryPageViewModel viewModel = new(store);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(HistoryPageState.DatabaseUnavailable, viewModel.State);
        Assert.Equal("History database unavailable.", viewModel.StatusText);
    }

    [Fact]
    public async Task ViewModelHonorsCancellationAndDisposal()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using CancellationTokenSource cancellation = new();
        FakeHistoryStore store = new(
            [new("app", "App", ApplicationDisposition.Portable, now)],
            _ =>
            {
                cancellation.Cancel();
                return new MetricHistoryQueryResult([], true);
            });
        using HistoryPageViewModel viewModel = new(store);

        await viewModel.InitializeAsync(cancellation.Token);

        Assert.Equal(HistoryPageState.Cancelled, viewModel.State);
        viewModel.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => viewModel.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NewSelectionCannotBeReplacedByStaleQueryResults()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        StaleHistoryStore store = new(now);
        using HistoryPageViewModel viewModel = new(store, () => now, TimeSpan.Zero, 360);
        await viewModel.InitializeAsync(CancellationToken.None);

        store.DelaySixHourQueries = true;
        Task stale = viewModel.SelectRangeAsync(viewModel.Ranges[4], CancellationToken.None);
        await store.SixHourStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        store.DelaySixHourQueries = false;
        Task current = viewModel.SelectRangeAsync(viewModel.Ranges[6], CancellationToken.None);
        await current;
        store.ReleaseSixHourQueries.SetResult();
        await stale;

        Assert.Equal("Selected range: 24 hours", viewModel.SelectedRangeText);
        Assert.All(viewModel.Charts, chart => Assert.Equal(24, Assert.Single(chart.Samples).Value));
    }

    private sealed class FakeHistoryStore(
        IReadOnlyList<MetricHistoryApplication> applications,
        Func<MetricHistoryMetric, MetricHistoryQueryResult> query)
        : IMetricHistoryStore
    {
        public List<MetricHistoryMetric> QueriedMetrics { get; } = [];

        public MetricHistoryStoreDiagnostics Diagnostics => new(0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        public ValueTask<MetricHistoryApplicationsResult> ListApplicationsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricHistoryApplicationsResult(applications, true));

        public ValueTask<MetricHistoryWriteResult> EnqueueAsync(
            IReadOnlyList<ApplicationMetricSnapshot> snapshots,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MetricHistoryWriteResult.Success);

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<MetricHistoryQueryResult> QueryAsync(
            string logicalApplicationId,
            MetricHistoryMetric metric,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueriedMetrics.Add(metric);
            return ValueTask.FromResult(query(metric));
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaleHistoryStore(DateTimeOffset now) : IMetricHistoryStore
    {
        private int _sixHourCalls;

        public bool DelaySixHourQueries { get; set; }

        public TaskCompletionSource SixHourStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSixHourQueries { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MetricHistoryStoreDiagnostics Diagnostics => new(0, 0, 0, 0, 0, 0, 0, 0, 0, null);

        public ValueTask<MetricHistoryApplicationsResult> ListApplicationsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MetricHistoryApplicationsResult(
                [new("app", "App", ApplicationDisposition.Installed, now)],
                true));

        public ValueTask<MetricHistoryWriteResult> EnqueueAsync(
            IReadOnlyList<ApplicationMetricSnapshot> snapshots,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MetricHistoryWriteResult.Success);

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<MetricHistoryQueryResult> QueryAsync(
            string logicalApplicationId,
            MetricHistoryMetric metric,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            int hours = (int)Math.Round((toUtc - fromUtc).TotalHours);
            if (hours == 6 && DelaySixHourQueries)
            {
                if (Interlocked.Increment(ref _sixHourCalls) == 11)
                {
                    SixHourStarted.SetResult();
                }

                await ReleaseSixHourQueries.Task;
            }

            return new(
                [new("app", "lifetime", now.AddMinutes(-1), metric, hours, MetricAvailability.Available, null, false)],
                true);
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
