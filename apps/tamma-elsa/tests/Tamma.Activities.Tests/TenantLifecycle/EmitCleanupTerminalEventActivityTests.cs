using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// H6 / Story 28-5 AC7 — tests for the terminal-event activity.
/// Verifies the failure-summary truncation rules (1900-char cap so
/// summaries fit inside <c>tenants.ProvisioningDetail</c>) and the
/// outcome-decision shape (full success vs partial failure).
///
/// <para>The activity itself runs against an Elsa
/// <c>ActivityExecutionContext</c> + a control-plane DbContext factory;
/// covering the row-update + event-emit paths end-to-end requires the
/// Elsa runtime, so this fixture targets the pure-helper logic and the
/// outcome decision derived from a populated
/// <see cref="ICleanupStateStore"/>.</para>
/// </summary>
[TestFixture]
public class EmitCleanupTerminalEventActivityTests
{
    [Test]
    public void BuildFailureSummary_ContainsAllFailedSteps()
    {
        var failedSteps = new[]
        {
            CleanupSteps.DropDatabase,
            CleanupSteps.DropRole,
        };
        var details = new Dictionary<string, string>
        {
            [CleanupSteps.DropDatabase] = "PostgresException: db in use",
            [CleanupSteps.DropRole] = "PostgresException: still owns objects",
        };

        var summary = EmitCleanupTerminalEventActivity.BuildFailureSummaryForTesting(
            failedSteps, details);

        summary.Should().Contain(CleanupSteps.DropDatabase);
        summary.Should().Contain(CleanupSteps.DropRole);
        summary.Should().Contain("db in use");
        summary.Should().Contain("still owns objects");
        summary.Should().StartWith("Cleanup partial — 2 step(s) failed:");
    }

    [Test]
    public void BuildFailureSummary_TruncatesAt1900Chars()
    {
        // tenants.ProvisioningDetail is varchar(2000) per the migration;
        // we cap at 1900 to leave headroom for diagnostic prefixes.
        // A pathological 100-step failure with 200-char details would
        // otherwise overflow the column.
        var failedSteps = Enumerable.Range(0, 100)
            .Select(i => $"step-{i}")
            .ToArray();
        var details = failedSteps
            .ToDictionary(
                s => s,
                s => $"VeryLongException: {new string('x', 200)}");

        var summary = EmitCleanupTerminalEventActivity.BuildFailureSummaryForTesting(
            failedSteps, details);

        summary.Length.Should().BeLessThanOrEqualTo(1900,
            "summary must fit comfortably within tenants.ProvisioningDetail");
    }

    [Test]
    public void BuildFailureSummary_HandlesMissingDetails()
    {
        // Defensive: if a step appears in failedSteps but not in
        // details (corrupted accumulator state, race condition), the
        // summary should still produce a valid, readable string.
        var failedSteps = new[] { CleanupSteps.EvictPool };
        var details = new Dictionary<string, string>(); // empty

        var summary = EmitCleanupTerminalEventActivity.BuildFailureSummaryForTesting(
            failedSteps, details);

        summary.Should().Contain(CleanupSteps.EvictPool);
        summary.Should().Contain("(no detail)");
    }

    [Test]
    public void TerminalEvent_OutcomeDerivedFromAccumulator_FullSuccess()
    {
        // Drives the same predicate the activity uses at runtime:
        // failedSteps.Count == 0 → DELETED.SUCCESS, else DELETE.FAILED.
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropDatabase);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropRole);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.SoftDeleteRow);

        CleanupWorkflowState.GetFailedSteps(store).Should().BeEmpty();
        CleanupWorkflowState.GetSucceededSteps(store).Should().HaveCount(4);
        // Activity's runtime check: failedSteps.Count == 0 → success.
    }

    [Test]
    public void TerminalEvent_OutcomeDerivedFromAccumulator_PartialFailure()
    {
        // Step 2 fails, the rest succeed → terminal must still fire
        // and report DELETE.FAILED with failedSteps=["drop-tenant-db"].
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordFailure(
            store, CleanupSteps.DropDatabase, "PgErr", "db in use");
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropRole);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.SoftDeleteRow);

        var failed = CleanupWorkflowState.GetFailedSteps(store);
        failed.Should().ContainSingle().Which.Should().Be(CleanupSteps.DropDatabase);
        CleanupWorkflowState.GetSucceededSteps(store).Should().HaveCount(3);
        // Activity's runtime check: failedSteps.Count > 0 → partial.
    }

    [Test]
    public void TerminalEvent_OutcomeDerivedFromAccumulator_AllStepsFailed()
    {
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.EvictPool, "X", "x");
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropDatabase, "Y", "y");
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropRole, "Z", "z");
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.SoftDeleteRow, "W", "w");

        CleanupWorkflowState.GetFailedSteps(store).Should().HaveCount(4);
        CleanupWorkflowState.GetSucceededSteps(store).Should().BeEmpty();

        // The terminal event fires DELETE.FAILED with the full
        // failedSteps list — the operator sees every failed step,
        // not just the first one.
    }
}
