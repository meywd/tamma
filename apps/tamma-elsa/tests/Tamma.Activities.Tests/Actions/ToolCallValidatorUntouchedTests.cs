using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Security;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-4 AC8 — <c>ToolCallValidator.ShellToolNames</c> is CHECKED from
/// outside, never DERIVED: deriving it from the catalog would delete twelve
/// defensive aliases and newly subject <c>run_tests</c> (which exposes a
/// <c>command</c> field) to ActionGate's regex denylist, producing
/// false-positive blocks. This fixture pins that 43-4's only edit to that file
/// is the internal <c>KnownShellToolNames</c> accessor.
/// </summary>
[TestFixture]
public class ToolCallValidatorUntouchedTests
{
    [Test]
    public void ShellToolNames_still_contains_exactly_the_thirteen_members()
    {
        ToolCallValidator.KnownShellToolNames.Should().BeEquivalentTo(new[]
        {
            "execute_shell_command", "run_command", "shell", "exec", "bash",
            "terminal", "run_shell", "execute_command", "system_command",
            "run_code", "execute", "cmd", "shell_execute",
        });
    }

    [Test]
    public void CommandFields_still_contains_exactly_the_six_members()
    {
        var field = typeof(ToolCallValidator).GetField(
            "CommandFields",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull("CommandFields moved — update this AC8 pin deliberately");

        ((string[])field!.GetValue(null)!).Should().BeEquivalentTo(
            new[] { "command", "cmd", "script", "code", "shell_command", "input" });
    }

    [Test]
    public void ToolCallValidator_references_neither_ActionCatalog_nor_ToolNameAliases()
    {
        // The validator checks the set from OUTSIDE; the set must not become
        // derived. Source-level scan (the compiled type cannot reference
        // Tamma.Api types anyway — Activities does not reference Api — but the
        // scan also catches an in-assembly re-derivation via the catalog).
        var root = RepoRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "Tamma.Activities", "Security", "ToolCallValidator.cs"));

        source.Should().NotContain("ActionCatalog");
        source.Should().NotContain("ToolNameAliases");
        source.Should().Contain("KnownShellToolNames",
            "the 43-4 accessor is the one permitted edit (D8)");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tamma.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate apps/tamma-elsa (Tamma.sln) from " + AppContext.BaseDirectory);
    }
}
