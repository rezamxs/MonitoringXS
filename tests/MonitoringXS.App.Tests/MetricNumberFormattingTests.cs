using System.Globalization;
using MonitoringXS.App.Controls;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

/// <summary>
/// Windows can run the English UI with a Persian regional format whose decimal
/// separator is '/'. Metric numbers must never pick that separator up from the
/// thread culture ("4/3 GB", "13/5 ms").
/// </summary>
public sealed class MetricNumberFormattingTests
{
    private const double Gibibyte = 1024d * 1024d * 1024d;
    private const double Mebibyte = 1024d * 1024d;

    [Fact]
    public void ChartAxisValuesIgnoreASlashDecimalRegionalFormat()
    {
        using CultureScope scope = CultureScope.SlashDecimal();

        Assert.Equal("4.3 GB", MetricAxisFormatter.Value(4.3 * Gibibyte, MetricSparklineScale.Dynamic, "bytes"));
        Assert.Equal("3.3 MB/s", MetricAxisFormatter.Value(3.3 * Mebibyte, MetricSparklineScale.Dynamic, "bytes/s"));
        Assert.Equal("100%", MetricAxisFormatter.Value(100, MetricSparklineScale.Percent, null));
        Assert.Equal("0 B", MetricAxisFormatter.Value(double.NaN, MetricSparklineScale.Dynamic, "bytes"));
    }

    [Fact]
    public void HistoryValuesIgnoreASlashDecimalRegionalFormat()
    {
        using CultureScope scope = CultureScope.SlashDecimal();

        Assert.Equal("4.1 GB", HistorySeriesPresentation.Format(4.1 * Gibibyte, HistoryValueKind.Bytes));
        Assert.Equal("37.1%", HistorySeriesPresentation.Format(37.1, HistoryValueKind.Percent));
        Assert.Equal("1.5 KB/s", HistorySeriesPresentation.Format(1536, HistoryValueKind.BytesPerSecond));
    }

    [Fact]
    public void AxisTimesFollowTheUiCultureLikeTheOtherTimestamps()
    {
        using CultureScope scope = CultureScope.SlashDecimal();
        CultureInfo english = CultureInfo.GetCultureInfo("en-US");
        DateTimeOffset timestamp = new(2026, 11, 27, 11, 54, 0, TimeSpan.Zero);

        string axis = MetricAxisFormatter.Time(timestamp, english);

        Assert.Equal(timestamp.ToLocalTime().ToString("t", english), axis);
        Assert.True(
            axis.EndsWith("AM", StringComparison.Ordinal) || axis.EndsWith("PM", StringComparison.Ordinal),
            axis);
    }

    [Fact]
    public void TooltipUsesTheSuppliedUiCultureAndInvariantNumbers()
    {
        using CultureScope scope = CultureScope.SlashDecimal();
        DateTimeOffset now = new(2026, 11, 27, 11, 54, 0, TimeSpan.Zero);
        CpuHistorySample[] samples = [new(now, 4.3 * Gibibyte, MetricAvailability.Available, null)];

        string tooltip = ChartTooltipBuilder.Build(
            samples,
            0,
            "Working set",
            HistoryValueKind.Bytes,
            "Available",
            "Partial",
            "Unavailable",
            "Reason",
            "Value",
            TestContext.Current.CancellationToken,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Contains("Value: 4.3 GB", tooltip, StringComparison.Ordinal);
        Assert.Contains(
            now.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("en-US")),
            tooltip,
            StringComparison.Ordinal);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture;
        private readonly CultureInfo _uiCulture;

        private CultureScope(CultureInfo culture)
        {
            _culture = CultureInfo.CurrentCulture;
            _uiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        }

        public static CultureScope SlashDecimal()
        {
            CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("fa-IR").Clone();
            culture.NumberFormat.NumberDecimalSeparator = "/";
            culture.NumberFormat.PercentDecimalSeparator = "/";
            return new CultureScope(culture);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
