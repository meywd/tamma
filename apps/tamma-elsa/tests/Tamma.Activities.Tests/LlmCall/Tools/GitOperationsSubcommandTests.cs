using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

/// <summary>
/// Story 43-4 (AC6/D6) — ExecuteAsync-level behaviour of the
/// <see cref="GitOperationsTool"/> subcommand allow-check after the
/// <c>GitSubcommand</c>-projection refactor. The vocabulary pins (literal
/// 14-name symmetric diff, comparer, derived description) live in
/// <c>Actions/GitSubcommandParitySweepTests</c>; this fixture proves the
/// RUNTIME contract did not move:
/// <list type="bullet">
/// <item>mixed-case subcommands stay ACCEPTED (bug
/// 2026-07-27-gitoperationstool-case-insensitive-subcommand-refactor-trap);</item>
/// <item>unlisted subcommands stay REJECTED with the same error shape.</item>
/// </list>
/// </summary>
[TestFixture]
public class GitOperationsSubcommandTests
{
    private string _workspaceRoot = null!;
    private GitOperationsTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot,
                ["ToolExecution:GitTimeoutSeconds"] = "10",
            })
            .Build();

        _tool = new GitOperationsTool(new Mock<ILogger<GitOperationsTool>>().Object, config);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    private async Task InitGitRepoAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
        {
            WorkingDirectory = _workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();
    }

    [TestCase("STATUS")]
    [TestCase("Status")]
    [TestCase("sTaTuS")]
    public async Task MixedCase_status_passes_the_allow_check(string subcommand)
    {
        // THE TRAP: the pre-refactor HashSet was OrdinalIgnoreCase, so these
        // spellings cleared the allow-check (git itself then decides what a
        // non-canonical spelling means — unchanged before/after). EnumWire
        // parsing alone is case-sensitive; the refactor must not have narrowed
        // the allow-check.
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use git CLI, skipping on Windows.");
            return;
        }

        await InitGitRepoAsync();

        var result = await _tool.ExecuteAsync("tc-case", $$"""{"subcommand": "{{subcommand}}"}""");

        result.Output.Should().NotContain("Unknown git subcommand",
            $"'{subcommand}' cleared the allow-check before the GitSubcommand refactor and must keep clearing it");
    }

    [Test]
    public async Task MixedCase_Push_passes_the_allow_check()
    {
        // "Push" must clear the subcommand allow-check (the pre-refactor
        // behaviour). With no remote configured the git call itself fails, but
        // the failure must be git's, not "Unknown git subcommand".
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Shell tests use git CLI, skipping on Windows.");
            return;
        }

        await InitGitRepoAsync();

        var result = await _tool.ExecuteAsync("tc-push", """{"subcommand": "Push"}""");

        result.Output.Should().NotContain("Unknown git subcommand",
            "'Push' was accepted before the GitSubcommand refactor and must stay accepted");
    }

    [TestCase("reset")]
    [TestCase("rebase")]
    [TestCase("clean")]
    [TestCase("filter-branch")]
    public async Task Unlisted_subcommands_stay_rejected(string subcommand)
    {
        var result = await _tool.ExecuteAsync("tc-rej", $$"""{"subcommand": "{{subcommand}}"}""");

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Unknown git subcommand",
            $"'{subcommand}' was never in the allow-set; the refactor must not have widened it");
        result.Output.Should().Contain("Allowed:");
    }

    [Test]
    public async Task Rejection_message_lists_the_canonical_subcommands()
    {
        var result = await _tool.ExecuteAsync("tc-msg", """{"subcommand": "reset"}""");

        result.Success.Should().BeFalse();
        foreach (var wire in new[]
                 {
                     "status", "diff", "log", "add", "commit", "push", "branch", "checkout",
                     "stash", "show", "fetch", "pull", "rev-parse", "ls-files",
                 })
        {
            result.Output.Should().Contain(wire);
        }
    }
}
