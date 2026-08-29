using MonitoringXS.Core.Models;

namespace MonitoringXS.Collectors.Tests;

public sealed class PhysicalDiskAggregationServiceTests
{
    [Fact]
    public void AggregateSumsPhysicalDiskProcessesWithinLogicalApplication()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(50, now);
        ProcessDescriptor second = Process(51, now);
        ApplicationIdentity app = App();

        PhysicalDiskMetricSet result = new PhysicalDiskAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [Sample(first, now, 10, 20, 100, 200), Sample(second, now, 30, 40, 300, 400)])[app.LogicalApplicationId];

        Assert.Equal(40d, result.ReadBytesPerSecond.Value);
        Assert.Equal(60d, result.WriteBytesPerSecond.Value);
        Assert.Equal(400UL, result.SessionReadBytes.Value);
        Assert.Equal(600UL, result.SessionWriteBytes.Value);
        Assert.Equal(2UL, result.SessionReadOperationCount.Value);
    }

    [Fact]
    public void MissingProcessSampleMakesApplicationValuePartial()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor first = Process(52, now);
        ProcessDescriptor second = Process(53, now);
        ApplicationIdentity app = App();

        PhysicalDiskMetricSet result = new PhysicalDiskAggregationService().Aggregate(
            [AttributionResult.Attributed(first, app), AttributionResult.Attributed(second, app)],
            [Sample(first, now, 10, 20, 100, 200)])[app.LogicalApplicationId];

        Assert.Equal(MetricAvailability.Partial, result.ReadBytesPerSecond.Availability);
        Assert.Contains("1 of 2", result.ReadBytesPerSecond.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregatePreservesBrowserEditorAndLauncherApplicationBoundaries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProcessDescriptor chrome = Process(60, now);
        ProcessDescriptor chromeHelper = Process(61, now);
        ProcessDescriptor code = Process(62, now);
        ProcessDescriptor codeHelper = Process(63, now);
        ProcessDescriptor unrelatedNode = Process(64, now);
        ProcessDescriptor steam = Process(65, now);
        ProcessDescriptor steamGame = Process(66, now);
        ProcessDescriptor epic = Process(67, now);
        ProcessDescriptor epicGame = Process(68, now);
        ApplicationIdentity chromeApp = App("google-chrome", "Google Chrome");
        ApplicationIdentity codeApp = App("visual-studio-code", "Visual Studio Code");
        ApplicationIdentity nodeApp = App("portable-node", "Node");
        ApplicationIdentity steamApp = App("steam", "Steam");
        ApplicationIdentity steamGameApp = App("steam-game", "Steam Game");
        ApplicationIdentity epicApp = App("epic-games-launcher", "Epic Games Launcher");
        ApplicationIdentity epicGameApp = App("epic-game", "Epic Game");
        AttributionResult[] attribution =
        [
            AttributionResult.Attributed(chrome, chromeApp),
            AttributionResult.Attributed(chromeHelper, chromeApp),
            AttributionResult.Attributed(code, codeApp),
            AttributionResult.Attributed(codeHelper, codeApp),
            AttributionResult.Attributed(unrelatedNode, nodeApp),
            AttributionResult.Attributed(steam, steamApp),
            AttributionResult.Attributed(steamGame, steamGameApp),
            AttributionResult.Attributed(epic, epicApp),
            AttributionResult.Attributed(epicGame, epicGameApp)
        ];
        PhysicalDiskProcessSample[] samples =
        [
            Sample(chrome, now, 10, 1, 100, 10),
            Sample(chromeHelper, now, 20, 2, 200, 20),
            Sample(code, now, 30, 3, 300, 30),
            Sample(codeHelper, now, 40, 4, 400, 40),
            Sample(unrelatedNode, now, 50, 5, 500, 50),
            Sample(steam, now, 60, 6, 600, 60),
            Sample(steamGame, now, 70, 7, 700, 70),
            Sample(epic, now, 80, 8, 800, 80),
            Sample(epicGame, now, 90, 9, 900, 90)
        ];

        IReadOnlyDictionary<string, PhysicalDiskMetricSet> result =
            new PhysicalDiskAggregationService().Aggregate(attribution, samples);

        Assert.Equal(30d, result[chromeApp.LogicalApplicationId].ReadBytesPerSecond.Value);
        Assert.Equal(70d, result[codeApp.LogicalApplicationId].ReadBytesPerSecond.Value);
        Assert.Equal(50d, result[nodeApp.LogicalApplicationId].ReadBytesPerSecond.Value);
        Assert.Equal(60d, result[steamApp.LogicalApplicationId].ReadBytesPerSecond.Value);
        Assert.Equal(70d, result[steamGameApp.LogicalApplicationId].ReadBytesPerSecond.Value);
        Assert.Equal(80d, result[epicApp.LogicalApplicationId].ReadBytesPerSecond.Value);
        Assert.Equal(90d, result[epicGameApp.LogicalApplicationId].ReadBytesPerSecond.Value);
    }

    private static PhysicalDiskProcessSample Sample(
        ProcessDescriptor process,
        DateTimeOffset capturedAt,
        double readRate,
        double writeRate,
        ulong readBytes,
        ulong writeBytes) => new(
            process.InstanceId,
            capturedAt,
            MetricValue<double>.Available(readRate),
            MetricValue<double>.Available(writeRate),
            MetricValue<ulong>.Available(readBytes),
            MetricValue<ulong>.Available(writeBytes),
            MetricValue<ulong>.Available(1),
            MetricValue<ulong>.Available(2),
            default);

    private static ProcessDescriptor Process(int pid, DateTimeOffset start) =>
        new(new ProcessInstanceId(pid, start), "test", null, null, null, null, null, null, false, true);

    private static ApplicationIdentity App() =>
        new("app", "App", null, ApplicationDisposition.Installed, null, ClassificationConfidence.High, "test");

    private static ApplicationIdentity App(string id, string name) =>
        new(id, name, null, ApplicationDisposition.Installed, null, ClassificationConfidence.High, "test");
}
