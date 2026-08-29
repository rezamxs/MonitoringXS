using System.Globalization;
using MonitoringXS.App.ViewModels;
using MonitoringXS.Core.Models;

namespace MonitoringXS.App.Tests;

public sealed class ApplicationSearchMatcherTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySearchRestoresCurrentCard(string? query)
    {
        Assert.True(ApplicationSearchMatcher.Matches(Card(), query, English));
    }

    [Theory]
    [InlineData("sample app")]
    [InlineData("SAMPLE.EXE")]
    [InlineData("contoso")]
    [InlineData("product title")]
    public void MatchesSafeApplicationMetadataCaseInsensitively(string query)
    {
        Assert.True(ApplicationSearchMatcher.Matches(Card(), query, English));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("C:\\Apps\\sample")]
    [InlineData("SAMPLE.EXE")]
    public void MatchesCapturedPidAndExecutablePathOnlyInMemory(string query)
    {
        ApplicationCardViewModel card = CardWithPath();

        Assert.True(ApplicationSearchMatcher.Matches(card, query, English));
    }

    [Fact]
    public void MissingExecutablePathDoesNotMatchOrThrow()
    {
        ApplicationCardViewModel card = Card();

        Assert.False(ApplicationSearchMatcher.Matches(card, @"C:\\Apps", English));
    }

    [Fact]
    public void MatchesPersianTextWithPersianCulture()
    {
        ApplicationCardViewModel card = Card(displayName: "ویرایشگر نمونه");

        Assert.True(ApplicationSearchMatcher.Matches(
            card,
            "نمونه",
            CultureInfo.GetCultureInfo("fa-IR")));
    }

    [Fact]
    public void NoResultAndClearAreDeterministic()
    {
        ApplicationCardViewModel card = Card();

        Assert.False(ApplicationSearchMatcher.Matches(card, "missing", English));
        Assert.True(ApplicationSearchMatcher.Matches(card, string.Empty, English));
    }

    [Fact]
    public void LiveRefreshUpdatesSearchMetadataWithoutReplacingCard()
    {
        ApplicationCardViewModel card = Card(displayName: "Before");
        Assert.False(ApplicationSearchMatcher.Matches(card, "After", English));

        card.Update(Snapshot("After"), []);

        Assert.True(ApplicationSearchMatcher.Matches(card, "After", English));
        Assert.Equal("sample", card.LogicalApplicationId);
    }

    [Fact]
    public void PackagedIdentityIsSearchableButPortableOpaqueIdentityIsNot()
    {
        ApplicationCardViewModel packaged = Card(
            disposition: ApplicationDisposition.Packaged,
            logicalId: "msix:Contoso.Sample_123");
        ApplicationCardViewModel portable = Card(
            disposition: ApplicationDisposition.Portable,
            logicalId: "portable:private-hash");

        Assert.True(ApplicationSearchMatcher.Matches(packaged, "Contoso.Sample", English));
        Assert.False(ApplicationSearchMatcher.Matches(portable, "private-hash", English));
    }

    private static ApplicationCardViewModel Card(
        string displayName = "Sample App",
        ApplicationDisposition disposition = ApplicationDisposition.Installed,
        string logicalId = "sample")
    {
        ApplicationCardViewModel card = new()
        {
            LogicalApplicationId = logicalId,
            Disposition = disposition
        };
        card.Update(Snapshot(displayName, disposition, logicalId), []);
        return card;
    }

    private static ApplicationCardViewModel CardWithPath()
    {
        ApplicationCardViewModel card = new()
        {
            LogicalApplicationId = "sample",
            Disposition = ApplicationDisposition.Installed
        };
        ApplicationMetricSnapshot snapshot = Snapshot("Sample App");
        ProcessDescriptor process = snapshot.Processes[0] with { ExecutablePath = @"C:\Apps\sample.exe" };
        card.Update(snapshot with { Processes = [process] }, []);
        return card;
    }

    private static ApplicationMetricSnapshot Snapshot(
        string displayName,
        ApplicationDisposition disposition = ApplicationDisposition.Installed,
        string logicalId = "sample") => new(
        new ApplicationIdentity(
            logicalId,
            displayName,
            "Contoso Publisher",
            disposition,
            null,
            ClassificationConfidence.High,
            "test"),
        DateTimeOffset.UtcNow,
        MetricValue<double>.Available(1),
        MetricValue<long>.Available(1),
        MetricValue<double>.Available(1),
        MetricValue<double>.Available(1),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(1),
        MetricValue<ulong>.Available(1),
        1,
        [new(
            new ProcessInstanceId(42, DateTimeOffset.UtcNow),
            "Sample.exe",
            null,
            "Product Title",
            null,
            "Contoso Publisher",
            null,
            null,
            false,
            true)]);
}
