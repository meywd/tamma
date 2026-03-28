using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class RunTestsToolTests
{
    private Mock<ILogger<RunTestsTool>> _loggerMock = null!;
    private string _workspaceRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<RunTestsTool>>();
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Test]
    public async Task ExecuteAsync_ValidCommand_CapturesOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use /bin/bash, skipping on Windows.");
            return;
        }

        // Arrange — use echo as a test command for simplicity
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot,
                ["ToolExecution:TestCommand"] = "echo 'tests passed'",
                ["ToolExecution:TestTimeoutSeconds"] = "5"
            })
            .Build();
        var tool = new RunTestsTool(_loggerMock.Object, config);

        // Act
        var result = await tool.ExecuteAsync("tc1", "{}");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("tests passed");
        result.Output.Should().Contain("Exit code: 0");
    }

    [Test]
    public async Task ExecuteAsync_Timeout_ReturnsError()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use /bin/bash, skipping on Windows.");
            return;
        }

        // Arrange — set very short timeout
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot,
                ["ToolExecution:TestCommand"] = "sleep 30",
                ["ToolExecution:TestTimeoutSeconds"] = "1"
            })
            .Build();
        var tool = new RunTestsTool(_loggerMock.Object, config);

        // Act
        var result = await tool.ExecuteAsync("tc2", "{}");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("timed out");
    }

    [Test]
    public async Task ExecuteAsync_CustomCommand_OverridesDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use /bin/bash, skipping on Windows.");
            return;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot,
                ["ToolExecution:TestTimeoutSeconds"] = "5"
            })
            .Build();
        var tool = new RunTestsTool(_loggerMock.Object, config);

        // Act — use a custom command via arguments
        var result = await tool.ExecuteAsync("tc3",
            """{"command": "echo custom_test_output"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("custom_test_output");
    }

    [Test]
    public void ToolName_IsRunTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot
            })
            .Build();
        var tool = new RunTestsTool(_loggerMock.Object, config);

        tool.ToolName.Should().Be("run_tests");
    }
}
