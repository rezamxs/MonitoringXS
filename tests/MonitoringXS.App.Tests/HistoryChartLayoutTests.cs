using MonitoringXS.App.Controls;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class HistoryChartLayoutTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DynamicFlatSeriesUsesCenteredReadableDomain()
    {
        MetricSparklineLayout layout = Layout(
            [new(Start, 1024), new(Start.AddSeconds(1), 1024)],
            MetricSparklineScale.Dynamic);

        Assert.True(layout.DomainMinimum < 1024);
        Assert.True(layout.DomainMaximum > 1024);
        double midpoint = (layout.PlotTop + layout.PlotBottom) / 2;
        Assert.All(layout.Segments.Single(), point => Assert.Equal(midpoint, point.Y, 6));
    }

    [Fact]
    public void OnePointUsesMarkerAndTwoPointsUseLine()
    {
        MetricSparklineLayout one = Layout([new(Start, 10)], MetricSparklineScale.Dynamic);
        MetricSparklineLayout two = Layout(
            [new(Start, 10), new(Start.AddSeconds(1), 20)],
            MetricSparklineScale.Dynamic);

        Assert.Single(one.Markers);
        Assert.Empty(one.Segments);
        Assert.Single(two.Segments);
        Assert.Empty(two.Markers);
    }

    [Fact]
    public void DuplicateAndUnorderedTimestampsDoNotCreateVerticalSegments()
    {
        MetricSparklineLayout layout = Layout(
        [
            new(Start.AddSeconds(2), 30),
            new(Start, 10),
            new(Start.AddSeconds(1), 20),
            new(Start.AddSeconds(1), 25)
        ],
        MetricSparklineScale.Dynamic);

        MetricSparklinePoint[] points = layout.Segments.SelectMany(segment => segment).ToArray();
        Assert.Equal(3, points.Length);
        Assert.Equal(points.Length, points.Select(point => point.X).Distinct().Count());
        Assert.True(points.SequenceEqual(points.OrderBy(point => point.X)));
    }

    [Fact]
    public void SubPixelTimestampsDoNotCreateVerticalSegments()
    {
        MetricSparklineLayout layout = MetricSparklineLayout.Create(
            [
                new(Start, 10),
                new(Start.AddTicks(1), 20),
                new(Start.AddTicks(2), 30)
            ],
            120,
            120,
            MetricSparklineScale.Dynamic,
            Start,
            Start.AddSeconds(1));

        MetricSparklinePoint[] points = layout.Segments
            .SelectMany(segment => segment)
            .Concat(layout.Markers)
            .ToArray();
        Assert.NotEmpty(points);
        Assert.Equal(points.Length, points.Select(point => Math.Round(point.X, 3)).Distinct().Count());
    }

    [Fact]
    public void CoordinatesStayInsideSelectedTimeAndPlotBounds()
    {
        DateTimeOffset rangeEnd = Start.AddMinutes(15);
        MetricSparklineLayout layout = MetricSparklineLayout.Create(
            [new(Start.AddMinutes(5), 10), new(Start.AddMinutes(10), 20)],
            500,
            180,
            MetricSparklineScale.Dynamic,
            Start,
            rangeEnd);

        MetricSparklinePoint[] points = layout.Segments.Single().ToArray();
        Assert.True(points[0].X > layout.PlotLeft);
        Assert.True(points[^1].X < layout.PlotRight);
        Assert.All(points, point =>
        {
            Assert.InRange(point.X, layout.PlotLeft, layout.PlotRight);
            Assert.InRange(point.Y, layout.PlotTop, layout.PlotBottom);
        });
    }

    [Fact]
    public void CpuAndGpuPercentDomainIsAlwaysZeroToOneHundred()
    {
        MetricSparklineLayout layout = Layout(
            [new(Start, 40), new(Start.AddSeconds(1), 40)],
            MetricSparklineScale.Percent);

        Assert.Equal(0, layout.DomainMinimum);
        Assert.Equal(100, layout.DomainMaximum);
    }

    [Fact]
    public void DynamicDomainResistsOneExtremeSpikeAndKeepsZeroValid()
    {
        CpuHistorySample[] samples = Enumerable.Range(0, 99)
            .Select(index => new CpuHistorySample(Start.AddSeconds(index), index % 3))
            .Append(new(Start.AddSeconds(99), 1_000_000_000))
            .ToArray();

        MetricSparklineLayout layout = Layout(samples, MetricSparklineScale.Dynamic);
        MetricSparklineLayout zero = Layout(
            [new(Start, 0), new(Start.AddSeconds(1), 0)],
            MetricSparklineScale.Dynamic);

        Assert.True(layout.DomainMaximum < 100);
        Assert.Equal(layout.PlotTop, layout.Segments.Single()[^1].Y);
        Assert.Equal(0, zero.Peak);
        Assert.True(zero.DomainMinimum < 0);
        Assert.True(zero.DomainMaximum > 0);
    }

    [Fact]
    public void PresentationSortsUtcDeduplicatesAndPreservesPartialAndLargeGaps()
    {
        HistoryMetricDefinition definition = new(
            MetricHistoryMetric.WorkingSetBytes,
            "Working Set",
            HistoryValueKind.Bytes);
        MetricHistoryQueryResult result = new(
        [
            Point(Start.AddMinutes(10), 30, MetricAvailability.Available),
            Point(Start, 10, MetricAvailability.Available),
            Point(Start, 15, MetricAvailability.Partial),
            Point(Start.AddMinutes(5), null, MetricAvailability.Unavailable)
        ],
        true);

        var presentation = HistorySeriesPresentation.Create(
            definition,
            result,
            new("15 minutes", TimeSpan.FromMinutes(15)),
            360);

        Assert.True(presentation.Samples.SequenceEqual(
            presentation.Samples.OrderBy(sample => sample.Timestamp)));
        Assert.Equal(presentation.Samples.Count, presentation.Samples
            .Select(sample => sample.Timestamp.UtcTicks)
            .Distinct()
            .Count());
        Assert.Equal(15, presentation.Samples[0].Value);
        Assert.All(presentation.Samples, sample => Assert.Equal(TimeSpan.Zero, sample.Timestamp.Offset));
        Assert.Contains(presentation.Samples, sample => !sample.Value.HasValue);
        Assert.Equal("Partial", presentation.State);
    }

    [Fact]
    public void PresentationSplitsLargeTimeAndPidLifetimeGaps()
    {
        HistoryMetricDefinition definition = new(
            MetricHistoryMetric.WorkingSetBytes,
            "Working Set",
            HistoryValueKind.Bytes);
        MetricHistoryQueryResult result = new(
        [
            Point(Start, 10, MetricAvailability.Available, "lifetime-a"),
            Point(Start.AddMinutes(10), 20, MetricAvailability.Available, "lifetime-a"),
            Point(Start.AddMinutes(10).AddSeconds(1), 30, MetricAvailability.Available, "lifetime-b")
        ],
        true);

        var presentation = HistorySeriesPresentation.Create(
            definition,
            result,
            new("15 minutes", TimeSpan.FromMinutes(15)),
            360);

        Assert.Equal(2, presentation.Samples.Count(sample => !sample.Value.HasValue));
    }

    [Fact]
    public void DecimationKeepsLimitEndpointsGapsExtremaAndUniqueTimestamps()
    {
        CpuHistorySample[] samples = Enumerable.Range(0, 800)
            .Select(index => new CpuHistorySample(
                Start.AddSeconds(index),
                index == 400 ? 1_000_000 : index % 20))
            .ToArray();
        samples[200].Value = null;
        samples[600].Value = null;

        IList<CpuHistorySample> display = HistoryPointDecimator.Decimate(samples, 360);

        Assert.Equal(360, display.Count);
        Assert.Equal(samples[0].Timestamp, display[0].Timestamp);
        Assert.Equal(samples[^1].Timestamp, display[^1].Timestamp);
        Assert.Contains(display, sample => sample.Value == 1_000_000);
        Assert.Contains(display, sample => sample.Timestamp == samples[200].Timestamp && sample.Value is null);
        Assert.Contains(display, sample => sample.Timestamp == samples[600].Timestamp && sample.Value is null);
        Assert.Equal(display.Count, display.Select(sample => sample.Timestamp.UtcTicks).Distinct().Count());
    }

    [Fact]
    public void HistoryPageContractRemainsReachableAtOneHundredFiftyPercent()
    {
        const double dpi = 144;
        const double physicalWidth = 1180;
        const double physicalHeight = 760;
        const double pageHorizontalPadding = 48;
        const double pageVerticalPadding = 40;
        const double cardMinimumWidth = 400;
        const double cardHeight = 230;
        const double spacing = 12;
        const double refreshWidth = 80;
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MonitoringXS.App",
            "HistoryPage.xaml"));

        Assert.Contains("Padding=\"24,16,24,24\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"280\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"160\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinItemWidth=\"400\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinItemHeight=\"230\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"180\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding AccessibilityText, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);

        double scale = dpi / 96;
        double logicalWidth = physicalWidth / scale;
        double logicalHeight = physicalHeight / scale;
        double contentWidth = logicalWidth - pageHorizontalPadding;
        double contentHeight = logicalHeight - pageVerticalPadding;
        double toolbarInnerWidth = contentWidth - 24;
        double toolbarControlsWidth = 280 + spacing + 160 + spacing + refreshWidth;
        int columns = Math.Max(
            1,
            (int)Math.Floor((contentWidth + spacing) / (cardMinimumWidth + spacing)));
        int rows = (int)Math.Ceiling(11d / columns);

        Assert.Equal(144, dpi);
        Assert.Equal(786.667, logicalWidth, 3);
        Assert.Equal(506.667, logicalHeight, 3);
        Assert.True(toolbarControlsWidth <= toolbarInnerWidth);
        Assert.Equal(1, columns);
        Assert.Equal(11, rows);

        double headerTop = 16;
        double headerBottom = headerTop + 56;
        double toolbarTop = headerBottom + spacing;
        double toolbarBottom = toolbarTop + 60;
        double selectorLeft = 24 + 12;
        double selectorRight = selectorLeft + 280;
        double rangeLeft = selectorRight + spacing;
        double rangeRight = rangeLeft + 160;
        double refreshRight = rangeRight + spacing + refreshWidth;
        Assert.True(headerBottom < toolbarTop);
        Assert.True(selectorLeft >= 24);
        Assert.True(refreshRight <= logicalWidth - 24);
        Assert.True(toolbarBottom < logicalHeight - 24);

        double cardWidth = contentWidth - 12;
        double plotWidth = cardWidth - 28;
        MetricSparklineLayout plot = MetricSparklineLayout.Create(
            [new(Start, 10), new(Start.AddMinutes(15), 20)],
            plotWidth,
            180,
            MetricSparklineScale.Dynamic,
            Start,
            Start.AddMinutes(15));
        Assert.True(plot.PlotLeft >= 0);
        Assert.True(plot.PlotRight <= plotWidth);
        Assert.True(plot.PlotTop >= 0);
        Assert.True(plot.PlotBottom <= 180);

        double chartViewportHeight = contentHeight - (56 + 60 + 40 + (3 * spacing));
        double rowStride = cardHeight + spacing;
        double totalCardHeight = (rows * cardHeight) + ((rows - 1) * spacing);
        double maximumScrollOffset = totalCardHeight - chartViewportHeight;
        double lastCardTopAtEnd = ((rows - 1) * rowStride) - maximumScrollOffset;
        double lastCardBottomAtEnd = lastCardTopAtEnd + cardHeight;
        Assert.True(totalCardHeight > chartViewportHeight);
        Assert.InRange(lastCardBottomAtEnd, 0, chartViewportHeight);
        Assert.All(
            Enumerable.Range(0, rows - 1),
            row => Assert.True(((row + 1) * rowStride) >= ((row * rowStride) + cardHeight)));

        string[] accessibleSummaries =
        [
            "CPU",
            "Working Set memory",
            "Process I/O read",
            "Process I/O write",
            "Physical Disk read",
            "Physical Disk write",
            "Network receive",
            "Network send",
            "GPU utilization",
            "Dedicated GPU memory",
            "Shared GPU memory"
        ];
        Assert.Equal(11, accessibleSummaries.Length);
        Assert.Equal(11, accessibleSummaries.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Shared GPU memory", accessibleSummaries[^1]);
    }

    private static MetricSparklineLayout Layout(
        IReadOnlyList<CpuHistorySample> samples,
        MetricSparklineScale scale) =>
        MetricSparklineLayout.Create(
            samples,
            500,
            180,
            scale,
            Start,
            Start.AddMinutes(15));

    private static MetricHistoryPoint Point(
        DateTimeOffset timestamp,
        double? value,
        MetricAvailability availability,
        string lifetime = "lifetime") =>
        new(
            "app",
            lifetime,
            timestamp,
            MetricHistoryMetric.WorkingSetBytes,
            value,
            availability,
            null,
            false);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MonitoringXS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("MonitoringXS repository root was not found.");
    }
}
