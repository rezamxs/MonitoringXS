using MonitoringXS.Core.Models;
using MonitoringXS.Storage.Settings;

namespace MonitoringXS.Storage.Tests;

public sealed class JsonApplicationSettingsStoreTests
{
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingFileReturnsDocumentedDefaults()
    {
        using TestDirectory directory = new();
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);

        ApplicationSettingsLoadResult result = await store.LoadAsync(TestCancellation);

        Assert.True(result.IsAvailable);
        Assert.False(result.Recovered);
        Assert.Equal(ApplicationSettings.Default, result.Settings);
        Assert.False(File.Exists(directory.SettingsPath));
    }

    [Fact]
    public async Task RoundTripUsesOneVersionedTypedDocument()
    {
        using TestDirectory directory = new();
        ApplicationSettings expected = new(1, 5, 168, ApplicationTheme.Dark);
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);

        Assert.True((await store.SaveAsync(expected, TestCancellation)).Succeeded);
        ApplicationSettingsLoadResult result = await store.LoadAsync(TestCancellation);
        string json = await File.ReadAllTextAsync(directory.SettingsPath, TestCancellation);

        Assert.Equal(expected, result.Settings);
        Assert.Contains("\"Version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"Theme\": \"Dark\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveSamplingInterval", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryRetention\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnoredForForwardCompatibleVersionOneDocuments()
    {
        using TestDirectory directory = new();
        await File.WriteAllTextAsync(
            directory.SettingsPath,
            """
            {
              "Version": 1,
              "LiveSamplingSeconds": 2,
              "HistoryRetentionHours": 72,
              "Theme": "Light",
              "UnknownFutureField": true
            }
            """,
            TestCancellation);
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);

        ApplicationSettingsLoadResult result = await store.LoadAsync(TestCancellation);

        Assert.True(result.IsAvailable);
        Assert.Equal(new(1, 2, 72, ApplicationTheme.Light), result.Settings);
    }

    [Fact]
    public async Task FutureVersionFailsClosedWithoutChangingItsFile()
    {
        using TestDirectory directory = new();
        const string future = """
            {
              "Version": 2,
              "LiveSamplingSeconds": 1,
              "HistoryRetentionHours": 24,
              "Theme": "System"
            }
            """;
        await File.WriteAllTextAsync(directory.SettingsPath, future, TestCancellation);
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);

        ApplicationSettingsLoadResult result = await store.LoadAsync(TestCancellation);

        Assert.False(result.IsAvailable);
        Assert.False(result.Recovered);
        Assert.Equal(ApplicationSettings.Default, result.Settings);
        Assert.Equal(future, await File.ReadAllTextAsync(
            directory.SettingsPath,
            TestCancellation));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""
        {
          "Version": 1,
          "LiveSamplingSeconds": 3,
          "HistoryRetentionHours": 24,
          "Theme": "System"
        }
        """)]
    [InlineData("""
        {
          "Version": 1,
          "LiveSamplingSeconds": 1,
          "HistoryRetentionHours": 24,
          "Theme": "Blue"
        }
        """)]
    public async Task CorruptOrInvalidDocumentIsQuarantined(string content)
    {
        using TestDirectory directory = new();
        await File.WriteAllTextAsync(directory.SettingsPath, content, TestCancellation);
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);

        ApplicationSettingsLoadResult result = await store.LoadAsync(TestCancellation);

        Assert.True(result.IsAvailable);
        Assert.True(result.Recovered);
        Assert.Equal(ApplicationSettings.Default, result.Settings);
        Assert.False(File.Exists(directory.SettingsPath));
        Assert.Single(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task ReplacementIsAtomicAndLeavesNoTemporaryFile()
    {
        using TestDirectory directory = new();
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);
        Assert.True((await store.SaveAsync(
            ApplicationSettings.Default,
            TestCancellation)).Succeeded);
        ApplicationSettings replacement = new(1, 2, 6, ApplicationTheme.Light);

        Assert.True((await store.SaveAsync(replacement, TestCancellation)).Succeeded);

        Assert.Equal(replacement, (await store.LoadAsync(TestCancellation)).Settings);
        Assert.False(File.Exists(directory.SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentWritesRemainValidAndBounded()
    {
        using TestDirectory directory = new();
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);
        ApplicationSettings[] values =
        [
            new(1, 1, 6, ApplicationTheme.System),
            new(1, 2, 24, ApplicationTheme.Light),
            new(1, 5, 168, ApplicationTheme.Dark)
        ];

        await Task.WhenAll(values.Select(value =>
            store.SaveAsync(value, TestCancellation).AsTask()));
        ApplicationSettingsLoadResult result = await store.LoadAsync(TestCancellation);

        Assert.Contains(result.Settings, values);
        Assert.False(File.Exists(directory.SettingsPath + ".tmp"));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CancellationAndIdempotentDisposalAreSafe()
    {
        using TestDirectory directory = new();
        JsonApplicationSettingsStore store = new(directory.SettingsPath);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.SaveAsync(ApplicationSettings.Default, cancelled.Token));
        Assert.False(File.Exists(directory.SettingsPath));
        await store.DisposeAsync();
        await store.DisposeAsync();
    }

    [Fact]
    public async Task StorageFailureIsReportedWithoutLeakingExceptionDetails()
    {
        using TestDirectory directory = new();
        Directory.CreateDirectory(directory.SettingsPath);
        await using JsonApplicationSettingsStore store = new(directory.SettingsPath);

        ApplicationSettingsSaveResult result = await store.SaveAsync(
            ApplicationSettings.Default,
            TestCancellation);

        Assert.False(result.Succeeded);
        Assert.Equal("Settings storage is unavailable.", result.Error);
        Assert.False(File.Exists(directory.SettingsPath + ".tmp"));
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MonitoringXS.Settings.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            SettingsPath = System.IO.Path.Combine(Path, "settings.json");
        }

        public string Path { get; }

        public string SettingsPath { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
