using MonitoringXS.Core.Models;
using MonitoringXS.Storage;
using MonitoringXS.Storage.Attribution;

namespace MonitoringXS.Storage.Tests;

public sealed class StorageMilestoneTests
{
    [Fact]
    public void StatusSeparatesAttributionOverridesFromFutureMetricHistory()
    {
        Assert.Contains("attribution overrides", StorageMilestone.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Milestone 5", StorageMilestone.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttributionOverridePersistsAcrossStoreInstancesAndCanBeRemoved()
    {
        string directory = CreateTestDirectory();
        string file = Path.Combine(directory, "overrides.json");
        try
        {
            UserAttributionOverride value = Override(@"C:\Tools\Example.exe", "user:example", "Example");
            using (JsonUserAttributionOverrideStore writer = new(file))
            {
                OverrideMutationResult saved = await writer.UpsertAsync(value, TestContext.Current.CancellationToken);
                Assert.True(saved.Succeeded, saved.Error);
            }

            using (JsonUserAttributionOverrideStore reader = new(file))
            {
                UserAttributionOverrideSnapshot snapshot = await reader.GetAllAsync(TestContext.Current.CancellationToken);
                Assert.True(snapshot.IsAvailable, snapshot.Error);
                UserAttributionOverride persisted = Assert.Single(snapshot.Overrides).Value;
                Assert.Equal("user:example", persisted.LogicalApplicationId);
                Assert.Equal("Example", persisted.DisplayName);

                OverrideMutationResult removed = await reader.RemoveAsync(value.ExecutablePath, TestContext.Current.CancellationToken);
                Assert.True(removed.Succeeded, removed.Error);
            }

            using JsonUserAttributionOverrideStore finalReader = new(file);
            Assert.Empty((await finalReader.GetAllAsync(TestContext.Current.CancellationToken)).Overrides);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AttributionOverrideCapacityIsBoundedWithoutDroppingExistingEntry()
    {
        string directory = CreateTestDirectory();
        string file = Path.Combine(directory, "overrides.json");
        try
        {
            using JsonUserAttributionOverrideStore store = new(file, capacity: 1);
            OverrideMutationResult first = await store.UpsertAsync(
                Override(@"C:\Tools\One.exe", "user:one", "One"),
                TestContext.Current.CancellationToken);
            OverrideMutationResult second = await store.UpsertAsync(
                Override(@"C:\Tools\Two.exe", "user:two", "Two"),
                TestContext.Current.CancellationToken);

            Assert.True(first.Succeeded, first.Error);
            Assert.False(second.Succeeded);
            UserAttributionOverride persisted = Assert.Single(
                (await store.GetAllAsync(TestContext.Current.CancellationToken)).Overrides).Value;
            Assert.Equal("user:one", persisted.LogicalApplicationId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidOverrideDocumentIsReportedAsUnavailable()
    {
        string directory = CreateTestDirectory();
        string file = Path.Combine(directory, "overrides.json");
        try
        {
            await File.WriteAllTextAsync(file, "not-json", TestContext.Current.CancellationToken);
            using JsonUserAttributionOverrideStore store = new(file);

            UserAttributionOverrideSnapshot snapshot = await store.GetAllAsync(TestContext.Current.CancellationToken);

            Assert.False(snapshot.IsAvailable);
            Assert.Empty(snapshot.Overrides);
            Assert.Contains("unavailable", snapshot.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MonitoringXS.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static UserAttributionOverride Override(string path, string id, string name) => new(
        path,
        id,
        name,
        null,
        ApplicationDisposition.Portable,
        DateTimeOffset.UtcNow);
}
