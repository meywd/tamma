using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Activities.Tests.Scheduling;

/// <summary>
/// Story 41-30 — the two source-text regression pins (AC3, AC7). Crude by
/// design: each reads a production source file as TEXT and asserts on
/// literals, because the failures they prevent are one-line "just add one
/// constant / one table name" edits that no compiled assertion would see.
/// </summary>
[TestFixture]
public class ScheduledTriggerSourcePinTests
{
    /// <summary>Walk up from the test bin dir to the apps/tamma-elsa root
    /// (the directory containing Tamma.sln).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tamma.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from within the apps/tamma-elsa tree");
        return dir!.FullName;
    }

    // ── AC3 — target-agnosticism: the service never names a consumer ──

    [Test]
    public void TenantScheduledTriggerService_Source_Contains_No_Consumer_DefinitionId_Literal()
    {
        var path = Path.Combine(RepoRoot(),
            "src", "Tamma.ElsaServer", "Workflows", "TenantScheduledTriggerService.cs");
        File.Exists(path).Should().BeTrue($"expected the seam's service at {path}");
        var source = File.ReadAllText(path);

        // The five Wave-2 consumers (41-11/41-16/41-17/41-20/41-23) — the
        // workflow definition id is ROW DATA; "just adding one" constant here
        // is the failure this pin exists to catch.
        foreach (var consumerId in new[]
        {
            "security-audit", "tech-debt-triage", "pr-triage-sweep",
            "capacity-review", "regression-management",
        })
        {
            source.Should().NotContain(consumerId,
                $"AC3 — the seam is target-agnostic; '{consumerId}' belongs in a "
                + "scheduled_triggers ROW (via the admin API), never in the dispatcher source");
        }

        source.Should().NotContain("HourlyAnalyticsRollupWorkflow.DefinitionId",
            "AC3 — the seam must not borrow the rollup scheduler's hardcoded target either");
    }

    // ── AC7 / D9 — the destructive startup DROP list excludes both tables ──

    [Test]
    public void Schedule_Tables_Are_Not_In_The_Destructive_Startup_DropList()
    {
        var path = Path.Combine(RepoRoot(), "src", "Tamma.Api", "Program.cs");
        File.Exists(path).Should().BeTrue($"expected the API composition root at {path}");
        var source = File.ReadAllText(path);

        // Locate the DROP statement literal (the Epic 19 wipe). Anchor on the
        // raw-SQL marker so the assertion scopes to the statement, not the
        // whole 3000-line file.
        var dropStart = source.IndexOf("DROP TABLE IF EXISTS", StringComparison.Ordinal);
        dropStart.Should().BeGreaterThan(0, "the Epic 19 startup wipe must still exist for this pin to guard");
        var dropEnd = source.IndexOf("CASCADE", dropStart, StringComparison.Ordinal);
        dropEnd.Should().BeGreaterThan(dropStart);
        var dropStatement = source[dropStart..dropEnd];

        dropStatement.Should().NotContain("scheduled_triggers",
            "AC7 — sweeping the schedule registry into the deploy wipe would silently "
            + "disable every tenant's recurring audits on every deploy");
        dropStatement.Should().NotContain("scheduled_trigger_fires",
            "AC7 — wiping the fire ledger would erase the at-most-once evidence across a deploy");
    }
}
