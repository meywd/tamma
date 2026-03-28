using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class GitOperationsToolTests
{
    private Mock<ILogger<GitOperationsTool>> _loggerMock = null!;
    private string _workspaceRoot = null!;
    private GitOperationsTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<GitOperationsTool>>();
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot,
                ["ToolExecution:GitTimeoutSeconds"] = "10"
            })
            .Build();

        _tool = new GitOperationsTool(_loggerMock.Object, config);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Test]
    public async Task ExecuteAsync_UnknownSubcommand_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc1",
            """{"subcommand": "rebase_interactive"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Unknown git subcommand");
        result.Output.Should().Contain("Allowed");
    }

    [Test]
    public async Task ExecuteAsync_RevParse_InGitRepo_ReturnsOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use git CLI, skipping on Windows.");
            return;
        }

        // Arrange — initialize a git repo in the workspace
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
        {
            WorkingDirectory = _workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();

        // Act
        var result = await _tool.ExecuteAsync("tc2",
            """{"subcommand": "rev-parse", "args": "--git-dir"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain(".git");
    }

    [Test]
    public async Task ExecuteAsync_Status_InGitRepo_ReturnsOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use git CLI, skipping on Windows.");
            return;
        }

        // Arrange — initialize a git repo
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
        {
            WorkingDirectory = _workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();

        // Act
        var result = await _tool.ExecuteAsync("tc3",
            """{"subcommand": "status"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ExecuteAsync_MissingSubcommandArgument_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc4", """{"command": "status"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("error");
    }

    [Test]
    public void ToolName_IsGitOperations()
    {
        _tool.ToolName.Should().Be("git_operations");
    }
}
