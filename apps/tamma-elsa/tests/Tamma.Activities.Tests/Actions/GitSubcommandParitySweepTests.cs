using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// REFLECTION SWEEP binding <see cref="GitSubcommand"/> to
/// <see cref="GitOperationsTool"/>'s LIVE private allow-set (Story 43-2 AC8/D11).
/// The Core-side parity test pins the enum against a literal snapshot; this one
/// reads the tool's actual <c>AllowedSubcommands</c> field, so drift between the
/// enum and the running security check fails CI in either direction. When Story
/// 43-4 replaces the HashSet with the enum this sweep is updated (or retired) in
/// the same commit.
/// </summary>
[TestFixture]
public class GitSubcommandParitySweepTests
{
    [Test]
    public void The_enum_equals_the_tools_live_allowed_subcommand_set()
    {
        var field = typeof(GitOperationsTool).GetField(
            "AllowedSubcommands", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull(
            "GitOperationsTool's private allow-set moved or was renamed — if Story 43-4 replaced it "
            + "with GitSubcommand, update/retire this sweep in the same commit");

        var live = (HashSet<string>)field!.GetValue(null)!;

        Enum.GetValues<GitSubcommand>().Select(g => g.ToWire())
            .Should().BeEquivalentTo(live,
                "fourteen in, fourteen out — the enum is a vocabulary refactor, never a policy change");
    }

    [Test]
    public void The_tools_description_restates_no_name_the_enum_lacks()
    {
        // GitOperationsTool.Description restates the 14 names as prose (43-2 C3);
        // until 43-4 derives it from the enum, pin that the restatement has not
        // drifted. ToolName/Description are expression-bodied constants, so an
        // uninitialized instance answers without a constructor.
        var tool = (GitOperationsTool)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(GitOperationsTool));

        foreach (var subcommand in Enum.GetValues<GitSubcommand>())
            tool.Description.Should().Contain(subcommand.ToWire());
    }
}
