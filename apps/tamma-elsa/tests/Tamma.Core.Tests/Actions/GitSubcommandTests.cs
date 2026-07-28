using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// <see cref="GitSubcommand"/> vocabulary contract (Story 43-2 AC8, D11):
/// fourteen in, fourteen out — byte-parity with the pre-refactor
/// <c>GitOperationsTool.AllowedSubcommands</c> contents, and a total read/write
/// grading. The literal list below is the ONLY place the old strings survive —
/// it is the parity oracle, deliberately not derived from the enum.
/// (Live parity against the tool's actual private set is asserted by
/// <c>GitSubcommandParitySweepTests</c> in Tamma.Activities.Tests, which can
/// reach the tool's assembly.)
/// </summary>
[TestFixture]
public class GitSubcommandTests
{
    /// <summary>Verbatim copy of GitOperationsTool.AllowedSubcommands at authoring time.</summary>
    private static readonly string[] PreRefactorHashSetContents =
    {
        "status", "diff", "log", "add", "commit", "push", "branch", "checkout",
        "stash", "show", "fetch", "pull", "rev-parse", "ls-files",
    };

    [Test]
    public void The_permitted_set_is_byte_identical_to_the_pre_refactor_hashset()
    {
        Enum.GetValues<GitSubcommand>().Select(g => g.ToWire())
            .Should().BeEquivalentTo(PreRefactorHashSetContents,
                "this is a vocabulary refactor with a count pin, not a policy change (43-2 D11)");
    }

    [Test]
    public void Every_member_has_a_grade()
    {
        foreach (var subcommand in Enum.GetValues<GitSubcommand>())
        {
            var act = () => subcommand.Grade();
            act.Should().NotThrow($"'{subcommand.ToWire()}' must be graded read or write");
        }
    }

    [Test]
    public void The_read_and_write_sets_are_pinned()
    {
        // Story 43-2 AC8's grading: the grade tracks remote/workspace-content
        // consequence (fetch and branch mutate only local refs).
        Enum.GetValues<GitSubcommand>()
            .Where(g => g.Grade() == GitSubcommandGrade.Read)
            .Select(g => g.ToWire())
            .Should().BeEquivalentTo(new[]
            {
                "status", "diff", "log", "show", "rev-parse", "ls-files", "fetch", "branch",
            });

        Enum.GetValues<GitSubcommand>()
            .Where(g => g.Grade() == GitSubcommandGrade.Write)
            .Select(g => g.ToWire())
            .Should().BeEquivalentTo(new[]
            {
                "add", "commit", "push", "checkout", "stash", "pull",
            });
    }
}
