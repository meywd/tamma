using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// H6 / Story 28-5 AC7 — verifies the per-step accumulator state used
/// by the cleanup <see cref="Elsa.Workflows.Activities.Sequence"/>.
///
/// <para>The whole purpose of <see cref="CleanupWorkflowState"/> is to
/// give each per-step activity a way to record success/failure into
/// workflow variables without throwing — so the next sibling step can
/// still run, and the terminal step can read the accumulated outcome.
/// These tests assert that contract: write a step, read it back; mix
/// successes and failures; verify the round-trip survives the JSON
/// serialization that the real Elsa runtime forces on us.</para>
/// </summary>
[TestFixture]
public class CleanupWorkflowStateTests
{
    [Test]
    public void RecordSuccess_AppendsToSucceededList_AndSetsFlag()
    {
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);

        CleanupWorkflowState.GetSucceededSteps(store).Should().Equal(CleanupSteps.EvictPool);
        store.GetBool(CleanupWorkflowVariables.SuccessFlag(CleanupSteps.EvictPool))
            .Should().Be(true);
        CleanupWorkflowState.GetFailedSteps(store).Should().BeEmpty();
        CleanupWorkflowState.GetStepDetails(store).Should().BeEmpty();
    }

    [Test]
    public void RecordSuccess_IsIdempotentAcrossRetries()
    {
        // The activity may emit a STEP_STARTED, do its work, get
        // suspended by Elsa mid-flight, and resume — recording success
        // twice. The terminal event must still see the step exactly
        // once (the dashboard timeline depends on it).
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);

        CleanupWorkflowState.GetSucceededSteps(store).Should()
            .ContainSingle().Which.Should().Be(CleanupSteps.EvictPool);
    }

    [Test]
    public void RecordFailure_AppendsToFailedList_AndStoresRedactedDetail()
    {
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.RecordFailure(
            store,
            CleanupSteps.DropDatabase,
            failureCode: "PostgresException",
            redactedDetail: "permission denied for database");

        CleanupWorkflowState.GetFailedSteps(store).Should()
            .Equal(CleanupSteps.DropDatabase);
        store.GetBool(CleanupWorkflowVariables.SuccessFlag(CleanupSteps.DropDatabase))
            .Should().Be(false);
        store.GetString(CleanupWorkflowVariables.FailureCode(CleanupSteps.DropDatabase))
            .Should().Be("PostgresException");
        var details = CleanupWorkflowState.GetStepDetails(store);
        details.Should().ContainKey(CleanupSteps.DropDatabase);
        details[CleanupSteps.DropDatabase].Should()
            .Be("PostgresException: permission denied for database");
    }

    [Test]
    public void RecordFailure_IsIdempotent()
    {
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropRole, "X", "first");
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropRole, "X", "first");

        CleanupWorkflowState.GetFailedSteps(store).Should()
            .ContainSingle().Which.Should().Be(CleanupSteps.DropRole);
    }

    [Test]
    public void RecordFailure_ReplacesDetailOnReRun()
    {
        // If a step is retried (Elsa suspends-then-replays the same
        // attempt) and fails again with a different message, the most
        // recent detail wins — operators want the latest error, not
        // an obsolete one.
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropRole, "TimeoutException", "10s elapsed");
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropRole, "PostgresException", "role still owns objects");

        var details = CleanupWorkflowState.GetStepDetails(store);
        details[CleanupSteps.DropRole].Should()
            .Be("PostgresException: role still owns objects",
                "the latest failure detail is the operator's actionable signal");
    }

    [Test]
    public void MixedSuccessFailure_AccumulatesIndependently()
    {
        // Step 1 succeeds, step 2 fails, step 3 succeeds, step 4 fails.
        // Both lists must reflect all four outcomes — this is the
        // exact partial-failure shape the terminal event needs to read.
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.DropDatabase, "PgErr", "could not drop");
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropRole);
        CleanupWorkflowState.RecordFailure(store, CleanupSteps.SoftDeleteRow, "DbUpdate", "concurrency");

        CleanupWorkflowState.GetSucceededSteps(store).Should()
            .BeEquivalentTo(new[] { CleanupSteps.EvictPool, CleanupSteps.DropRole });
        CleanupWorkflowState.GetFailedSteps(store).Should()
            .BeEquivalentTo(new[] { CleanupSteps.DropDatabase, CleanupSteps.SoftDeleteRow });
        CleanupWorkflowState.GetStepDetails(store).Should().HaveCount(2);
    }

    [Test]
    public void EmptyStore_ReadsAsEmptyCollections()
    {
        var store = new InMemoryCleanupStateStore();

        CleanupWorkflowState.GetSucceededSteps(store).Should().BeEmpty();
        CleanupWorkflowState.GetFailedSteps(store).Should().BeEmpty();
        CleanupWorkflowState.GetStepDetails(store).Should().BeEmpty();
    }

    [Test]
    public void CorruptedListJson_ReadsAsEmpty()
    {
        // Round-trip resilience: if Elsa's variable bag round-tripped
        // through somewhere that mangled the JSON, we get a clean
        // empty read instead of a thrown JsonException that takes the
        // terminal-event activity down with it.
        var store = new InMemoryCleanupStateStore();
        store.SetString(CleanupWorkflowVariables.FailedStepsJson, "{ this is not a json array }");

        CleanupWorkflowState.GetFailedSteps(store).Should().BeEmpty();
    }

    [Test]
    public void CorruptedDictJson_ReadsAsEmpty()
    {
        var store = new InMemoryCleanupStateStore();
        store.SetString(CleanupWorkflowVariables.StepDetailsJson, "[]");  // wrong shape

        CleanupWorkflowState.GetStepDetails(store).Should().BeEmpty();
    }

    [Test]
    public void VariableNames_AreStable()
    {
        // The dashboard / future cross-process readers may key off
        // these names; they're part of the workflow-public contract.
        // Lock them in via tests so a casual rename triggers a clear
        // failure.
        CleanupWorkflowVariables.FailedStepsJson.Should()
            .Be("Tenant.CleanupStep.FailedSteps");
        CleanupWorkflowVariables.SucceededStepsJson.Should()
            .Be("Tenant.CleanupStep.SucceededSteps");
        CleanupWorkflowVariables.StepDetailsJson.Should()
            .Be("Tenant.CleanupStep.StepDetails");
        CleanupWorkflowVariables.SuccessFlag("evict-pool").Should()
            .Be("Tenant.CleanupStep.evict-pool.Success");
        CleanupWorkflowVariables.FailureCode("evict-pool").Should()
            .Be("Tenant.CleanupStep.evict-pool.FailureCode");
    }

    [Test]
    public void StepNameConstants_AreStable()
    {
        // Same rationale: emitted as tags on platform_events rows,
        // queried by the dashboard. Don't rename.
        CleanupSteps.EvictPool.Should().Be("evict-pool");
        CleanupSteps.DropDatabase.Should().Be("drop-tenant-db");
        CleanupSteps.DropRole.Should().Be("drop-tenant-role");
        CleanupSteps.SoftDeleteRow.Should().Be("soft-delete-cp-row");
    }
}
