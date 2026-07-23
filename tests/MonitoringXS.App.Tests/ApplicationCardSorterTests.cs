using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationCardSorterTests
{
    [Fact]
    public void SortsApplicationNamesInBothDirections()
    {
        ApplicationCardViewModel beta = Card("beta", "Beta", cpu: MetricValue<double>.Available(1));
        ApplicationCardViewModel alpha = Card("alpha", "Alpha", cpu: MetricValue<double>.Available(2));

        IReadOnlyList<ApplicationCardViewModel> ascending = ApplicationCardSorter.Sort(
            [beta, alpha],
            ApplicationSortField.ApplicationName,
            descending: false);
        IReadOnlyList<ApplicationCardViewModel> descending = ApplicationCardSorter.Sort(
            [beta, alpha],
            ApplicationSortField.ApplicationName,
            descending: true);

        Assert.Equal(["Alpha", "Beta"], ascending.Select(card => card.DisplayName));
        Assert.Equal(["Beta", "Alpha"], descending.Select(card => card.DisplayName));
    }

    [Fact]
    public void MetricSortKeepsUnavailableValuesAfterValidValuesInBothDirections()
    {
        ApplicationCardViewModel unavailable = Card(
            "unavailable",
            "Unavailable",
            cpu: MetricValue<double>.Unavailable(MetricAvailability.WarmingUp));
        ApplicationCardViewModel low = Card("low", "Low", cpu: MetricValue<double>.Available(2));
        ApplicationCardViewModel high = Card("high", "High", cpu: MetricValue<double>.Available(12));

        IReadOnlyList<ApplicationCardViewModel> ascending = ApplicationCardSorter.Sort(
            [unavailable, high, low],
            ApplicationSortField.CpuUsage,
            descending: false);
        IReadOnlyList<ApplicationCardViewModel> descending = ApplicationCardSorter.Sort(
            [unavailable, low, high],
            ApplicationSortField.CpuUsage,
            descending: true);

        Assert.Equal(["Low", "High", "Unavailable"], ascending.Select(card => card.DisplayName));
        Assert.Equal(["High", "Low", "Unavailable"], descending.Select(card => card.DisplayName));
    }

    [Theory]
    [InlineData(ApplicationSortField.CpuUsage)]
    [InlineData(ApplicationSortField.MemoryUsage)]
    [InlineData(ApplicationSortField.ProcessIoRate)]
    [InlineData(ApplicationSortField.PhysicalDiskRate)]
    [InlineData(ApplicationSortField.NetworkRate)]
    [InlineData(ApplicationSortField.ProcessCount)]
    public void SortsEveryNumericFieldInBothDirections(ApplicationSortField field)
    {
        ApplicationCardViewModel low = Card(
            "low",
            "Low",
            cpu: MetricValue<double>.Available(1),
            memoryBytes: MetricValue<long>.Available(1),
            ioRead: MetricValue<double>.Available(1),
            ioWrite: MetricValue<double>.Available(1),
            physicalRead: MetricValue<double>.Available(1),
            physicalWrite: MetricValue<double>.Available(1),
            networkDownload: MetricValue<double>.Available(1),
            networkUpload: MetricValue<double>.Available(1),
            processCount: 1);
        ApplicationCardViewModel high = Card(
            "high",
            "High",
            cpu: MetricValue<double>.Available(2),
            memoryBytes: MetricValue<long>.Available(2),
            ioRead: MetricValue<double>.Available(2),
            ioWrite: MetricValue<double>.Available(2),
            physicalRead: MetricValue<double>.Available(2),
            physicalWrite: MetricValue<double>.Available(2),
            networkDownload: MetricValue<double>.Available(2),
            networkUpload: MetricValue<double>.Available(2),
            processCount: 2);

        IReadOnlyList<ApplicationCardViewModel> ascending = ApplicationCardSorter.Sort(
            [high, low],
            field,
            descending: false);
        IReadOnlyList<ApplicationCardViewModel> descending = ApplicationCardSorter.Sort(
            [low, high],
            field,
            descending: true);

        Assert.Equal(["Low", "High"], ascending.Select(card => card.DisplayName));
        Assert.Equal(["High", "Low"], descending.Select(card => card.DisplayName));
    }

    [Fact]
    public void MetricTiesUseApplicationNameAsStableSecondaryOrder()
    {
        ApplicationCardViewModel zulu = Card("zulu", "Zulu", memoryBytes: MetricValue<long>.Available(256));
        ApplicationCardViewModel alpha = Card("alpha", "Alpha", memoryBytes: MetricValue<long>.Available(256));

        IReadOnlyList<ApplicationCardViewModel> result = ApplicationCardSorter.Sort(
            [zulu, alpha],
            ApplicationSortField.MemoryUsage,
            descending: true);

        Assert.Equal(["Alpha", "Zulu"], result.Select(card => card.DisplayName));
        Assert.Same(alpha, result[0]);
        Assert.Same(zulu, result[1]);
    }

    [Fact]
    public void PartialMetricSortsByItsMeasuredLowerBoundInsteadOfZero()
    {
        ApplicationCardViewModel partial = Card(
            "partial",
            "Partial",
            cpu: MetricValue<double>.Partial(8, "Lower bound."));
        ApplicationCardViewModel available = Card("available", "Available", cpu: MetricValue<double>.Available(4));
        ApplicationCardViewModel denied = Card(
            "denied",
            "Denied",
            cpu: MetricValue<double>.Unavailable(MetricAvailability.AccessDenied));

        IReadOnlyList<ApplicationCardViewModel> result = ApplicationCardSorter.Sort(
            [denied, available, partial],
            ApplicationSortField.CpuUsage,
            descending: true);

        Assert.Equal(["Partial", "Available", "Denied"], result.Select(card => card.DisplayName));
    }

    [Theory]
    [InlineData(ApplicationSortField.ProcessIoRate)]
    [InlineData(ApplicationSortField.PhysicalDiskRate)]
    [InlineData(ApplicationSortField.NetworkRate)]
    public void RateSortUsesCombinedReadWriteOrDownloadUploadValue(ApplicationSortField field)
    {
        ApplicationCardViewModel lower = Card(
            "lower",
            "Lower",
            ioRead: MetricValue<double>.Available(40),
            ioWrite: MetricValue<double>.Available(10),
            physicalRead: MetricValue<double>.Available(40),
            physicalWrite: MetricValue<double>.Available(10),
            networkDownload: MetricValue<double>.Available(40),
            networkUpload: MetricValue<double>.Available(10));
        ApplicationCardViewModel higher = Card(
            "higher",
            "Higher",
            ioRead: MetricValue<double>.Available(20),
            ioWrite: MetricValue<double>.Available(60),
            physicalRead: MetricValue<double>.Available(20),
            physicalWrite: MetricValue<double>.Available(60),
            networkDownload: MetricValue<double>.Available(20),
            networkUpload: MetricValue<double>.Available(60));

        IReadOnlyList<ApplicationCardViewModel> result = ApplicationCardSorter.Sort(
            [lower, higher],
            field,
            descending: true);

        Assert.Equal(["Higher", "Lower"], result.Select(card => card.DisplayName));
    }

    [Fact]
    public void SortsProcessCountWithoutReplacingCardInstances()
    {
        ApplicationCardViewModel one = Card("one", "One", processCount: 1);
        ApplicationCardViewModel four = Card("four", "Four", processCount: 4);

        IReadOnlyList<ApplicationCardViewModel> result = ApplicationCardSorter.Sort(
            [one, four],
            ApplicationSortField.ProcessCount,
            descending: true);

        Assert.Same(four, result[0]);
        Assert.Same(one, result[1]);
    }

    [Theory]
    [InlineData(ApplicationSortField.CpuUsage)]
    [InlineData(ApplicationSortField.MemoryUsage)]
    [InlineData(ApplicationSortField.ProcessIoRate)]
    [InlineData(ApplicationSortField.PhysicalDiskRate)]
    [InlineData(ApplicationSortField.NetworkRate)]
    public void ReportsNoComparableDataWhenEveryMetricIsUnavailable(ApplicationSortField field)
    {
        ApplicationCardViewModel warmingUp = CardWithAvailability(
            "warming",
            "Warming",
            MetricAvailability.WarmingUp);
        ApplicationCardViewModel denied = CardWithAvailability(
            "denied",
            "Denied",
            MetricAvailability.AccessDenied);

        Assert.False(ApplicationCardSorter.HasComparableData([warmingUp, denied], field));
    }

    [Theory]
    [InlineData(ApplicationSortField.CpuUsage)]
    [InlineData(ApplicationSortField.MemoryUsage)]
    [InlineData(ApplicationSortField.ProcessIoRate)]
    [InlineData(ApplicationSortField.PhysicalDiskRate)]
    [InlineData(ApplicationSortField.NetworkRate)]
    [InlineData(ApplicationSortField.ProcessCount)]
    public void ReportsComparableDataWhenARealValueExists(ApplicationSortField field)
    {
        ApplicationCardViewModel denied = CardWithAvailability(
            "denied",
            "Denied",
            MetricAvailability.AccessDenied);
        ApplicationCardViewModel available = Card("available", "Available");

        Assert.True(ApplicationCardSorter.HasComparableData([denied, available], field));
    }

    [Fact]
    public void AllUnavailableMetricValuesUseDeterministicNameOrder()
    {
        ApplicationCardViewModel zulu = CardWithAvailability(
            "zulu",
            "Zulu",
            MetricAvailability.Unavailable);
        ApplicationCardViewModel alpha = CardWithAvailability(
            "alpha",
            "Alpha",
            MetricAvailability.AccessDenied);

        IReadOnlyList<ApplicationCardViewModel> result = ApplicationCardSorter.Sort(
            [zulu, alpha],
            ApplicationSortField.NetworkRate,
            descending: true);

        Assert.Equal(["Alpha", "Zulu"], result.Select(card => card.DisplayName));
    }

    private static ApplicationCardViewModel CardWithAvailability(
        string id,
        string name,
        MetricAvailability availability) =>
        Card(
            id,
            name,
            cpu: MetricValue<double>.Unavailable(availability),
            memoryBytes: MetricValue<long>.Unavailable(availability),
            ioRead: MetricValue<double>.Unavailable(availability),
            ioWrite: MetricValue<double>.Unavailable(availability),
            physicalRead: MetricValue<double>.Unavailable(availability),
            physicalWrite: MetricValue<double>.Unavailable(availability),
            networkDownload: MetricValue<double>.Unavailable(availability),
            networkUpload: MetricValue<double>.Unavailable(availability));

    private static ApplicationCardViewModel Card(
        string id,
        string name,
        MetricValue<double>? cpu = null,
        MetricValue<long>? memoryBytes = null,
        MetricValue<double>? ioRead = null,
        MetricValue<double>? ioWrite = null,
        MetricValue<double>? physicalRead = null,
        MetricValue<double>? physicalWrite = null,
        MetricValue<double>? networkDownload = null,
        MetricValue<double>? networkUpload = null,
        int processCount = 1)
    {
        ApplicationCardViewModel card = new()
        {
            LogicalApplicationId = id,
            Disposition = ApplicationDisposition.Installed
        };
        card.Update(new ApplicationMetricSnapshot(
            new ApplicationIdentity(
                id,
                name,
                null,
                ApplicationDisposition.Installed,
                null,
                ClassificationConfidence.High,
                "test"),
            DateTimeOffset.UtcNow,
            cpu ?? MetricValue<double>.Available(0),
            memoryBytes ?? MetricValue<long>.Available(0),
            ioRead ?? MetricValue<double>.Available(0),
            ioWrite ?? MetricValue<double>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            MetricValue<ulong>.Available(0),
            processCount,
            [])
        {
            PhysicalDisk = new PhysicalDiskMetricSet(
                physicalRead ?? MetricValue<double>.Available(0),
                physicalWrite ?? MetricValue<double>.Available(0),
                MetricValue<ulong>.Available(0),
                MetricValue<ulong>.Available(0),
                MetricValue<ulong>.Available(0),
                MetricValue<ulong>.Available(0),
                default),
            Network = new NetworkMetricSet(
                networkDownload ?? MetricValue<double>.Available(0),
                networkUpload ?? MetricValue<double>.Available(0),
                MetricValue<ulong>.Available(0),
                MetricValue<ulong>.Available(0),
                MetricValue<int>.Available(0),
                MetricValue<int>.Available(0),
                default)
        }, []);
        return card;
    }
}
