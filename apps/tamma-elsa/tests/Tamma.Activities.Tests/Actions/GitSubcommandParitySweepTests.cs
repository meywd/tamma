using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// UPDATED BY STORY 43-4 (AC6/D6), in the same change that replaced
/// <see cref="GitOperationsTool"/>'s hand-written <c>AllowedSubcommands</c>
/// HashSet with a projection over <see cref="GitSubcommand"/> — exactly as this
/// sweep's pre-43-4 header prescribed. The enum is now the tool's source, so an
/// enum↔live-set comparison would be self-referential; the drift guards that
/// still bind to something real are:
///
/// <list type="number">
/// <item>the LITERAL pre-refactor snapshot (fourteen names copied from the
/// pre-43-4 <c>GitOperationsTool.cs</c>, not from the enum) — a wrong
/// <see cref="GitSubcommand"/> edit now fails here rather than silently
/// widening or narrowing what git can do;</item>
/// <item>the projection's COMPARER — the pre-refactor set matched
/// case-insensitively, so <c>"STATUS"</c>/<c>"Push"</c> must stay accepted (bug
/// 2026-07-27-gitoperationstool-case-insensitive-subcommand-refactor-trap);</item>
/// <item>the <c>Description</c> prose, now DERIVED from the enum rather than
/// restated.</item>
/// </list>
///
/// Behavioural (ExecuteAsync-level) parity lives in
/// <c>Tamma.Activities.Tests/LlmCall/Tools/GitOperationsSubcommandTests</c>.
/// </summary>
[TestFixture]
public class GitSubcommandParitySweepTests
{
    /// <summary>
    /// The pre-43-4 literal allow-set, copied VERBATIM from
    /// <c>GitOperationsTool.cs:21-25</c> as it stood before the refactor. Never
    /// regenerate this from <see cref="GitSubcommand"/> — the whole point is
    /// that it does not move when the enum does.
    /// </summary>
    private static readonly string[] PreRefactorLiteralNames =
    {
        "status", "diff", "log", "add", "commit", "push", "branch", "checkout",
        "stash", "show", "fetch", "pull", "rev-parse", "ls-files",
    };

    private static HashSet<string> LiveAllowedSubcommands()
    {
        var field = typeof(GitOperationsTool).GetField(
            "AllowedSubcommands", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull(
            "GitOperationsTool's allow-set moved or was renamed — update this sweep in the same "
            + "commit as that refactor (the 43-4 posture this file already went through once)");

        return (HashSet<string>)field!.GetValue(null)!;
    }

    [Test]
    public void The_live_allow_set_equals_the_pre_refactor_literal_names()
    {
        // Symmetric diff against the LITERALS, naming the drifted member:
        // fourteen in, fourteen out — the enum projection is a vocabulary
        // refactor, never a policy change (43-2 AC8/D11, 43-4 AC6/D6).
        var live = LiveAllowedSubcommands();

        live.Except(PreRefactorLiteralNames, StringComparer.OrdinalIgnoreCase)
            .Should().BeEmpty("no git subcommand may be silently ADDED — check GitSubcommand for the extra member");
        PreRefactorLiteralNames.Except(live, StringComparer.OrdinalIgnoreCase)
            .Should().BeEmpty("no git subcommand may be silently REMOVED — check GitSubcommand for the missing member");
        live.Should().HaveCount(14);
    }

    [Test]
    public void The_enum_wire_set_equals_the_pre_refactor_literal_names()
    {
        // The enum itself is pinned to the same literals, so the projection
        // cannot drift at either end.
        Enum.GetValues<GitSubcommand>().Select(g => g.ToWire())
            .Should().BeEquivalentTo(PreRefactorLiteralNames);
    }

    [Test]
    public void The_live_allow_set_stays_case_insensitive()
    {
        // THE TRAP (bug 2026-07-27): the pre-refactor HashSet used
        // StringComparer.OrdinalIgnoreCase, so "STATUS"/"Push" were accepted.
        // EnumWire parsing alone is ordinal case-sensitive; the projection must
        // keep the comparer or previously-valid calls start being rejected.
        var live = LiveAllowedSubcommands();

        live.Contains("STATUS").Should().BeTrue("'STATUS' was accepted before the enum refactor");
        live.Contains("Push").Should().BeTrue("'Push' was accepted before the enum refactor");
        live.Contains("Rev-Parse").Should().BeTrue("mixed-case was accepted before the enum refactor");
        live.Comparer.Should().BeSameAs(StringComparer.OrdinalIgnoreCase);
    }

    [Test]
    public void The_tools_description_derives_every_enum_name_exactly_once()
    {
        // 43-4 AC6: Description is now GENERATED from the enum, not restated.
        // ToolName/Description are expression-bodied, so an uninitialized
        // instance answers without a constructor.
        var tool = (GitOperationsTool)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(GitOperationsTool));

        foreach (var subcommand in Enum.GetValues<GitSubcommand>())
        {
            var wire = subcommand.ToWire();
            // "status" occurs inside no other wire name except itself; count
            // word-boundary-ish occurrences by splitting on the list separators.
            tool.Description.Should().Contain(wire);
        }

        var listed = tool.Description
            .Split(':').Last()
            .TrimEnd('.')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        listed.Should().BeEquivalentTo(PreRefactorLiteralNames,
            "the description's subcommand list is derived from GitSubcommand and lists each member exactly once");
    }
}
