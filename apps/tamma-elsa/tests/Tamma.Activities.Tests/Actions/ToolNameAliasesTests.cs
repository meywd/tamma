using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-4 AC1 — the resolution-only alias map. Every registry name resolves
/// (identity), the six Claude-Code advertised names resolve to the four expected
/// members, matching is OrdinalIgnoreCase (the ToolExecutorRegistry comparer),
/// unknown names return false without throwing, and the git read/write split is
/// pinned per subcommand. The companion guarantee — that the map changes NO
/// advertised name — is <c>AdvertisedToolNamesUnchangedTests</c>.
/// </summary>
[TestFixture]
public class ToolNameAliasesTests
{
    private static ActionKey Tool(ToolAction t) => new(ActionNamespace.Tool, t.ToWire());

    [TestCase("file_read", ToolAction.FileRead)]
    [TestCase("file_write", ToolAction.FileWrite)]
    [TestCase("search_code", ToolAction.SearchCode)]
    [TestCase("shell_execute", ToolAction.ShellExecute)]
    [TestCase("run_tests", ToolAction.RunTests)]
    [TestCase("get_acceptance_rules", ToolAction.GetAcceptanceRules)]
    public void Every_registry_name_resolves_to_itself(string name, ToolAction expected)
    {
        ToolNameAliases.TryResolve(name, out var key).Should().BeTrue();
        key.Should().Be(Tool(expected));
    }

    [TestCase("Read", ToolAction.FileRead)]
    [TestCase("Write", ToolAction.FileWrite)]
    [TestCase("Edit", ToolAction.FileWrite)]
    [TestCase("Bash", ToolAction.ShellExecute)]
    [TestCase("Grep", ToolAction.SearchCode)]
    [TestCase("Glob", ToolAction.SearchCode)]
    public void The_claude_code_names_resolve_to_the_four_expected_members(string name, ToolAction expected)
    {
        ToolNameAliases.TryResolve(name, out var key).Should().BeTrue();
        key.Should().Be(Tool(expected));
    }

    [TestCase("bash")]
    [TestCase("BASH")]
    [TestCase("Bash")]
    public void Matching_is_ordinal_ignore_case(string spelling)
    {
        // The same comparer as ToolExecutorRegistry's executor dictionary — a
        // name the registry would dispatch is a name the map resolves.
        ToolNameAliases.TryResolve(spelling, out var key).Should().BeTrue();
        key.Should().Be(Tool(ToolAction.ShellExecute));
    }

    [TestCase("Frobnicate")]
    [TestCase("")]
    [TestCase("mcp__server__tool")]
    public void An_unknown_name_returns_false_and_does_not_throw(string name)
    {
        var act = () => ToolNameAliases.TryResolve(name, out _);
        act.Should().NotThrow();
        ToolNameAliases.TryResolve(name, out _).Should().BeFalse();
    }

    [Test]
    public void Bare_git_operations_resolves_to_the_stricter_write_member()
    {
        ToolNameAliases.TryResolve("git_operations", out var key).Should().BeTrue();
        key.Should().Be(Tool(ToolAction.GitOperationsWrite),
            "an ungraded git call must never be graded as a read (fail-safe)");
    }

    // The read/write split, pinned per member (43-2 AC8's grades).
    [TestCase("status", ToolAction.GitOperationsRead)]
    [TestCase("diff", ToolAction.GitOperationsRead)]
    [TestCase("log", ToolAction.GitOperationsRead)]
    [TestCase("show", ToolAction.GitOperationsRead)]
    [TestCase("rev-parse", ToolAction.GitOperationsRead)]
    [TestCase("ls-files", ToolAction.GitOperationsRead)]
    [TestCase("fetch", ToolAction.GitOperationsRead)]
    [TestCase("branch", ToolAction.GitOperationsRead)]
    [TestCase("add", ToolAction.GitOperationsWrite)]
    [TestCase("commit", ToolAction.GitOperationsWrite)]
    [TestCase("push", ToolAction.GitOperationsWrite)]
    [TestCase("checkout", ToolAction.GitOperationsWrite)]
    [TestCase("stash", ToolAction.GitOperationsWrite)]
    [TestCase("pull", ToolAction.GitOperationsWrite)]
    public void TryResolveGit_pins_the_read_write_split(string subcommand, ToolAction expected)
    {
        ToolNameAliases.TryResolveGit(subcommand, out var key).Should().BeTrue();
        key.Should().Be(Tool(expected));
    }

    [TestCase("STATUS", ToolAction.GitOperationsRead)]
    [TestCase("Push", ToolAction.GitOperationsWrite)]
    public void TryResolveGit_tolerates_the_tools_case_insensitive_posture(string subcommand, ToolAction expected)
    {
        // The 2026-07-27 comparer trap, applied to the GATE side: a casing the
        // tool accepts must not become a casing-dependent gate path.
        ToolNameAliases.TryResolveGit(subcommand, out var key).Should().BeTrue();
        key.Should().Be(Tool(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("rebase")]
    public void TryResolveGit_grades_unknown_or_missing_subcommands_as_write(string? subcommand)
    {
        ToolNameAliases.TryResolveGit(subcommand, out var key).Should().BeTrue();
        key.Should().Be(Tool(ToolAction.GitOperationsWrite));
    }

    [Test]
    public void Every_alias_target_is_a_catalogued_tool_member()
    {
        foreach (var (name, key) in ToolNameAliases.All)
        {
            key.Ns.Should().Be(ActionNamespace.Tool, $"alias '{name}' must map into the tool plane");
            ActionCatalog.TryGet(key, out _).Should().BeTrue(
                $"alias '{name}' maps to '{key.ToWire()}', which must be catalogued");
        }
    }
}
