using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Attribution;

namespace MonitoringXS.IntegrationTests;

public sealed class ApplicationAttributionServiceTests
{
    private readonly ApplicationAttributionService _service = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddMinutes(-2);

    [Theory]
    [InlineData("chrome", "google-chrome")]
    [InlineData("msedge", "microsoft-edge")]
    public void BrowserProcessesAreGroupedByLogicalIdentity(string executable, string expectedId)
    {
        ProcessDescriptor[] processes = [Process(10, executable, true), Process(11, executable, false)];

        AttributionResult[] results = _service.Attribute(processes).ToArray();

        Assert.All(results, item => Assert.Equal(expectedId, item.Application?.LogicalApplicationId));
    }

    [Fact]
    public void SteamGameRemainsSeparateFromLauncher()
    {
        ProcessDescriptor steam = Process(20, "steam", true, @"C:\Program Files (x86)\Steam\steam.exe");
        ProcessDescriptor game = Process(21, "ExampleGame", true, @"C:\Program Files (x86)\Steam\steamapps\common\Example Game\ExampleGame.exe", parent: 20);

        AttributionResult[] results = _service.Attribute([steam, game]).ToArray();

        Assert.Equal("steam", results[0].Application?.LogicalApplicationId);
        Assert.NotEqual(results[0].Application?.LogicalApplicationId, results[1].Application?.LogicalApplicationId);
    }

    [Fact]
    public void EpicGameRemainsSeparateFromLauncher()
    {
        ProcessDescriptor launcher = Process(30, "EpicGamesLauncher", true, @"C:\Program Files\Epic Games\Launcher\EpicGamesLauncher.exe");
        ProcessDescriptor game = Process(31, "ExampleGame", true, @"C:\Program Files\Epic Games\ExampleGame\Binaries\ExampleGame.exe", parent: 30);

        AttributionResult[] results = _service.Attribute([launcher, game]).ToArray();

        Assert.Equal("epic-games-launcher", results[0].Application?.LogicalApplicationId);
        Assert.NotEqual(results[0].Application?.LogicalApplicationId, results[1].Application?.LogicalApplicationId);
    }

    [Fact]
    public void DirectVsCodeNodeHelperIsGroupedButUnrelatedNodeIsNot()
    {
        ProcessDescriptor code = Process(40, "Code", true, @"C:\Users\dev\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        ProcessDescriptor helper = Process(41, "node", false, @"C:\Users\dev\AppData\Local\Programs\Microsoft VS Code\node.exe", parent: 40, product: "Node.js");
        ProcessDescriptor unrelated = Process(42, "node", true, @"C:\dev\node.exe", product: "Node.js");

        AttributionResult[] results = _service.Attribute([code, helper, unrelated]).ToArray();

        Assert.Equal("visual-studio-code", results[1].Application?.LogicalApplicationId);
        Assert.NotEqual("visual-studio-code", results[2].Application?.LogicalApplicationId);
    }

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("notepad")]
    [InlineData("CalculatorApp")]
    public void UserFacingMicrosoftApplicationsRemainVisible(string executable)
    {
        AttributionResult result = Assert.Single(_service.Attribute([Process(50, executable, true, @"C:\Windows\System32\app.exe")]));

        Assert.False(result.IsHidden);
        Assert.NotNull(result.Application);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    [InlineData("services")]
    public void CriticalInfrastructureIsHidden(string executable)
    {
        AttributionResult result = Assert.Single(_service.Attribute([Process(60, executable, false, @"C:\Windows\System32\system.exe")]));

        Assert.True(result.IsHidden);
    }

    [Fact]
    public void SignedPortableToolIsPlacedInPortableDisposition()
    {
        ProcessDescriptor tool = Process(70, "UsefulTool", true, @"C:\Tools\UsefulTool.exe", product: "Useful Tool", publisher: "Example Publisher");

        AttributionResult result = Assert.Single(_service.Attribute([tool]));

        Assert.Equal(ApplicationDisposition.Portable, result.Application?.Disposition);
    }

    private ProcessDescriptor Process(
        int pid,
        string name,
        bool visible,
        string? path = null,
        int? parent = null,
        string? product = null,
        string? publisher = null) =>
        new(new ProcessInstanceId(pid, _start.AddMilliseconds(pid)), name, path, product, product, publisher, visible ? name : null, parent, false, visible);
}
