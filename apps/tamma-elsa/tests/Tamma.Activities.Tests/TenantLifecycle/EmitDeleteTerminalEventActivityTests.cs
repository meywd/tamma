using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 item #3 — tests for the delete-workflow terminal activity
/// <see cref="EmitDeleteTerminalEventActivity"/>. Mirrors the cleanup
/// sibling's coverage: the failure-summary truncation rules + the
/// outcome-decision shape (full success → DELETED.SUCCESS, any failure →
/// DELETE.FAILED). The row-update + event-emit paths require the Elsa
/// runtime, so this fixture targets the pure-helper logic and the outcome
/// derived from a populated <see cref="ICleanupStateStore"/>.
/// </summary>
[TestFixture]
public class EmitDeleteTerminalEventActivityTests
{
    [Test]
    public void Activity_IsNotAStep_AndEmitsTerminalEventType()
    {
        var activity = new EmitDeleteTerminalEventActivity();
        activity.Should().NotBeAssignableTo<CleanupStepActivity>(
            "the terminal emits the single terminal event, not the per-step tuple");
        activity.EventType.Should().Be("TENANT.DELETE.TERMINAL");
    }

    [Test]
    public void BuildFailureSummary_ContainsAllFailedSteps()
    {
        var failedSteps = new[]
        {
            CleanupSteps.DropSchema,
            CleanupSteps.DropRole,
        };
        var details = new Dictionary<string, string>
        {
            [CleanupSteps.DropSchema] = "drop_schema_failed: db in use",
            [CleanupSteps.DropRole] = "drop_role_failed: still owns objects",
        };

        var summary = EmitDeleteTerminalEventActivity.BuildFailureSummaryForTesting(
            failedSteps, details);

        summary.Should().Contain(CleanupSteps.DropSchema);
        summary.Should().Contain(CleanupSteps.DropRole);
        summary.Should().Contain("db in use");
        summary.Should().StartWith("Delete partial — 2 step(s) failed:");
    }

    [Test]
    public void BuildFailureSummary_TruncatesAt1900Chars()
    {
        var failedSteps = Enumerable.Range(0, 100).Select(i => $"step-{i}").ToArray();
        var details = failedSteps.ToDictionary(s => s, s => $"X: {new string('x', 200)}");

        var summary = EmitDeleteTerminalEventActivity.BuildFailureSummaryForTesting(
            failedSteps, details);

        summary.Length.Should().BeLessThanOrEqualTo(1900,
            "summary must fit within tenants.ProvisioningDetail");
    }

    [Test]
    public void BuildFailureSummary_HandlesMissingDetails()
    {
        var failedSteps = new[] { CleanupSteps.DropSchema };
        var details = new Dictionary<string, string>();

        var summary = EmitDeleteTerminalEventActivity.BuildFailureSummaryForTesting(
            failedSteps, details);

        summary.Should().Contain(CleanupSteps.DropSchema);
        summary.Should().Contain("(no detail)");
    }

    [Test]
    public void TerminalOutcome_AllStepsSucceeded_IsSuccess()
    {
        // Drives the same predicate the activity uses: failedSteps.Count == 0.
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.MarkDeleting);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.BackupDatabase);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropSchema);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropRole);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.CleanupRelationships);

        CleanupWorkflowState.GetFailedSteps(store).Should().BeEmpty();
        CleanupWorkflowState.GetSucceededSteps(store).Should().HaveCount(6);
    }

    [Test]
    public void TerminalOutcome_AnyStepFailed_IsFailure()
    {
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.MarkDeleting);
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.EvictPool);
        CleanupWorkflowState.RecordFailure(
            store, CleanupSteps.DropSchema, "drop_schema_failed", "db in use");
        CleanupWorkflowState.RecordSuccess(store, CleanupSteps.DropRole);

        var failed = CleanupWorkflowState.GetFailedSteps(store);
        failed.Should().ContainSingle().Which.Should().Be(CleanupSteps.DropSchema);
    }
}
