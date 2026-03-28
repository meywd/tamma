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

    [Test]
    public async Task ExecuteAsync_ArgsWithPipe_ReturnsBlocked()
    {
        // Act — inject a pipe metacharacter in args
        var result = await _tool.ExecuteAsync("tc5",
            """{"subcommand": "log", "args": "--oneline | cat /etc/passwd"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("metacharacters");
    }

    [Test]
    public async Task ExecuteAsync_ArgsWithSemicolon_ReturnsBlocked()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc6",
            """{"subcommand": "status", "args": "; rm -rf /"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("metacharacters");
    }

    [Test]
    public async Task ExecuteAsync_ArgsWithDollarSign_ReturnsBlocked()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc7",
            """{"subcommand": "log", "args": "$HOME"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("metacharacters");
    }

    [Test]
    public async Task ExecuteAsync_ArgsWithBackticks_ReturnsBlocked()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc8",
            """{"subcommand": "log", "args": "`whoami`"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("metacharacters");
    }

    [Test]
    public async Task ExecuteAsync_ArgsWithAmpersand_ReturnsBlocked()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc9",
            """{"subcommand": "status", "args": "&& rm -rf /"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("metacharacters");
    }

    [Test]
    public async Task ExecuteAsync_CleanArgs_Succeeds()
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

        // Act — clean args with dashes and normal characters
        var result = await _tool.ExecuteAsync("tc10",
            """{"subcommand": "log", "args": "--oneline -n 5"}""");

        // Assert — should not be blocked (may fail due to no commits, but not blocked)
        result.Output.Should().NotContain("metacharacters");
    }
}
