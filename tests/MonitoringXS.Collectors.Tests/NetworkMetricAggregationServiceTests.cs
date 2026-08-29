using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class NetworkMetricAggregationServiceTests
{
    [Fact]
    public void AggregatePreservesBrowserEditorLauncherAndGameBoundaries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor chrome = Process(200, now);
        ProcessDescriptor chromeHelper = Process(201, now);
        ProcessDescriptor edge = Process(202, now);
        ProcessDescriptor code = Process(203, now);
        ProcessDescriptor codeHelper = Process(204, now);
        ProcessDescriptor unrelatedNode = Process(205, now);
        ProcessDescriptor steam = Process(206, now);
        ProcessDescriptor steamGame = Process(207, now);
        ProcessDescriptor epic = Process(208, now);
        ProcessDescriptor epicGame = Process(209, now);
        ApplicationIdentity chromeApp = App("google-chrome", "Google Chrome");
        ApplicationIdentity edgeApp = App("microsoft-edge", "Microsoft Edge");
        ApplicationIdentity codeApp = App("visual-studio-code", "Visual Studio Code");
        ApplicationIdentity nodeApp = App(
            "portable-node",
            "Node",
            ApplicationDisposition.Portable);
        ApplicationIdentity steamApp = App("steam", "Steam");
        ApplicationIdentity steamGameApp = App("steam-game", "Steam Game");
        ApplicationIdentity epicApp = App("epic-games-launcher", "Epic Games Launcher");
        ApplicationIdentity epicGameApp = App("epic-game", "Epic Game");
        AttributionResult[] attribution =
        [
            AttributionResult.Attributed(chrome, chromeApp),
            AttributionResult.Attributed(chromeHelper, chromeApp),
            AttributionResult.Attributed(edge, edgeApp),
            AttributionResult.Attributed(code, codeApp),
            AttributionResult.Attributed(codeHelper, codeApp),
            AttributionResult.Attributed(unrelatedNode, nodeApp),
            AttributionResult.Attributed(steam, steamApp),
            AttributionResult.Attributed(steamGame, steamGameApp),
            AttributionResult.Attributed(epic, epicApp),
            AttributionResult.Attributed(epicGame, epicGameApp)
        ];
        NetworkProcessSample[] samples =
        [
            Sample(chrome, now, 10, 1),
            Sample(chromeHelper, now, 20, 2),
            Sample(edge, now, 25, 3),
            Sample(code, now, 30, 4),
            Sample(codeHelper, now, 40, 5),
            Sample(unrelatedNode, now, 50, 6),
            Sample(steam, now, 60, 7),
            Sample(steamGame, now, 70, 8),
            Sample(epic, now, 80, 9),
            Sample(epicGame, now, 90, 10)
        ];

        IReadOnlyDictionary<string, NetworkMetricSet> result =
            new NetworkMetricAggregationService().Aggregate(attribution, samples);

        Assert.Equal(30d, result[chromeApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(25d, result[edgeApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(70d, result[codeApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(50d, result[nodeApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(60d, result[steamApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(70d, result[steamGameApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(80d, result[epicApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(90d, result[epicGameApp.LogicalApplicationId].DownloadBytesPerSecond.Value);
        Assert.Equal(ApplicationDisposition.Portable, nodeApp.Disposition);
    }

    [Fact]
    public void MissingProcessSampleMakesApplicationNetworkValuePartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(210, now);
        ProcessDescriptor second = Process(211, now);
        ApplicationIdentity application = App("browser", "Browser");

        NetworkMetricSet result = new NetworkMetricAggregationService().Aggregate(
            [
                AttributionResult.Attributed(first, application),
                AttributionResult.Attributed(second, application)
            ],
            [Sample(first, now, 10, 20)])[application.LogicalApplicationId];

        Assert.Equal(MetricAvailability.Partial, result.DownloadBytesPerSecond.Availability);
        Assert.Contains("1 of 2", result.DownloadBytesPerSecond.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitedHelperTotalsRemainWithStillRunningLogicalApplication()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor browser = Process(212, now);
        ProcessDescriptor helper = Process(213, now);
        ApplicationIdentity application = App("browser", "Browser");
        NetworkMetricAggregationService aggregation = new();

        NetworkMetricSet first = aggregation.Aggregate(
            [
                AttributionResult.Attributed(browser, application),
                AttributionResult.Attributed(helper, application)
            ],
            [
                Sample(browser, now, 100, 10),
                Sample(helper, now, 200, 20)
            ])[application.LogicalApplicationId];
        NetworkMetricSet afterHelperExit = aggregation.Aggregate(
            [AttributionResult.Attributed(browser, application)],
            [Sample(browser, now.AddSeconds(1), 150, 15)])[application.LogicalApplicationId];
        NetworkMetricSet nextInterval = aggregation.Aggregate(
            [AttributionResult.Attributed(browser, application)],
            [Sample(browser, now.AddSeconds(2), 175, 17)])[application.LogicalApplicationId];

        Assert.Equal(300UL, first.SessionDownloadedBytes.Value);
        Assert.Equal(350UL, afterHelperExit.SessionDownloadedBytes.Value);
        Assert.Equal(35UL, afterHelperExit.SessionUploadedBytes.Value);
        Assert.Equal(150d, afterHelperExit.DownloadBytesPerSecond.Value);
        Assert.Equal(375UL, nextInterval.SessionDownloadedBytes.Value);
        Assert.Equal(37UL, nextInterval.SessionUploadedBytes.Value);
    }

    [Fact]
    public void EntireApplicationExitResetsRetainedLogicalSessionTotals()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor firstRun = Process(214, now);
        ProcessDescriptor secondRun = Process(214, now.AddMinutes(1));
        ApplicationIdentity application = App("browser", "Browser");
        NetworkMetricAggregationService aggregation = new();

        _ = aggregation.Aggregate(
            [AttributionResult.Attributed(firstRun, application)],
            [Sample(firstRun, now, 100, 10)]);
        Assert.Empty(aggregation.Aggregate([], []));
        NetworkMetricSet restarted = aggregation.Aggregate(
            [AttributionResult.Attributed(secondRun, application)],
            [Sample(secondRun, now.AddMinutes(1), 5, 1)])[application.LogicalApplicationId];

        Assert.Equal(5UL, restarted.SessionDownloadedBytes.Value);
        Assert.Equal(1UL, restarted.SessionUploadedBytes.Value);
    }

    [Fact]
    public void AttributionChangeDoesNotTransferHistoricalProcessTotals()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor process = Process(215, now);
        ApplicationIdentity firstApplication = App("first-app", "First App");
        ApplicationIdentity secondApplication = App("second-app", "Second App");
        NetworkMetricAggregationService aggregation = new();

        _ = aggregation.Aggregate(
            [AttributionResult.Attributed(process, firstApplication)],
            [Sample(process, now, 100, 10)]);
        NetworkMetricSet reclassified = aggregation.Aggregate(
            [AttributionResult.Attributed(process, secondApplication)],
            [Sample(process, now.AddSeconds(1), 150, 15)])[secondApplication.LogicalApplicationId];

        Assert.Equal(50UL, reclassified.SessionDownloadedBytes.Value);
        Assert.Equal(5UL, reclassified.SessionUploadedBytes.Value);
    }

    [Fact]
    public void RetentionLimitNeverEvictsActiveStateAndRecoversAsLowerBound()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NetworkMetricAggregationService aggregation = new();
        (AttributionResult Attribution, NetworkProcessSample Sample)[] initial = Enumerable
            .Range(0, 512)
            .Select(index =>
            {
                ProcessDescriptor process = Process(1_000 + index, now);
                ApplicationIdentity application = App($"app-{index}", $"App {index}");
                return (
                    AttributionResult.Attributed(process, application),
                    Sample(process, now, 1, 1));
            })
            .ToArray();
        _ = aggregation.Aggregate(
            initial.Select(item => item.Attribution).ToArray(),
            initial.Select(item => item.Sample).ToArray());
        ProcessDescriptor overflowProcess = Process(2_000, now);
        ApplicationIdentity overflowApplication = App("overflow-app", "Overflow App");
        AttributionResult[] withOverflowAttribution =
        [
            .. initial.Select(item => item.Attribution),
            AttributionResult.Attributed(overflowProcess, overflowApplication)
        ];
        NetworkProcessSample[] withOverflowSamples =
        [
            .. initial.Select(item => item.Sample),
            Sample(overflowProcess, now, 1, 1)
        ];

        IReadOnlyDictionary<string, NetworkMetricSet> atLimit = aggregation.Aggregate(
            withOverflowAttribution,
            withOverflowSamples);

        Assert.Equal(1UL, atLimit["app-0"].SessionDownloadedBytes.Value);
        Assert.Equal(
            MetricAvailability.Unavailable,
            atLimit[overflowApplication.LogicalApplicationId].SessionDownloadedBytes.Availability);

        IReadOnlyDictionary<string, NetworkMetricSet> afterSlotAvailable = aggregation.Aggregate(
            [
                .. initial.Skip(1).Select(item => item.Attribution),
                AttributionResult.Attributed(overflowProcess, overflowApplication)
            ],
            [
                .. initial.Skip(1).Select(item => item.Sample with
                {
                    SessionDownloadedBytes = MetricValue<ulong>.Available(2),
                    SessionUploadedBytes = MetricValue<ulong>.Available(2)
                }),
                Sample(overflowProcess, now.AddSeconds(1), 2, 2)
            ]);

        Assert.Equal(2UL, afterSlotAvailable["app-1"].SessionDownloadedBytes.Value);
        Assert.Equal(
            MetricAvailability.Partial,
            afterSlotAvailable[overflowApplication.LogicalApplicationId].SessionDownloadedBytes.Availability);
        Assert.Equal(1UL, afterSlotAvailable[overflowApplication.LogicalApplicationId].SessionDownloadedBytes.Value);
    }

    private static NetworkProcessSample Sample(
        ProcessDescriptor process,
        DateTimeOffset capturedAt,
        double receiveRate,
        double sendRate) => new(
        process.InstanceId,
        capturedAt,
        MetricValue<double>.Available(receiveRate),
        MetricValue<double>.Available(sendRate),
        MetricValue<ulong>.Available((ulong)receiveRate),
        MetricValue<ulong>.Available((ulong)sendRate),
        MetricValue<int>.Available(0),
        MetricValue<int>.Available(0),
        default);

    private static ProcessDescriptor Process(int pid, DateTimeOffset start) =>
        new(
            new ProcessInstanceId(pid, start),
            "test",
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            true);

    private static ApplicationIdentity App(
        string id,
        string name,
        ApplicationDisposition disposition = ApplicationDisposition.Installed) =>
        new(id, name, null, disposition, null, ClassificationConfidence.High, "test");
}
