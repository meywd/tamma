using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Tests.Workflows;
using Tamma.Activities.TenantLifecycle;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 item #1 + #3 — structural assertions on the rebuilt
/// <see cref="DeleteTenantWorkflow"/>:
///
/// <list type="bullet">
///   <item><description>Root is a <see cref="Sequence"/> that starts with
///     the delete-event trigger (item #1 — the bridge from
///     <c>TENANT.DELETE.REQUESTED</c> fires this).</description></item>
///   <item><description>The destructive steps are continue-on-error
///     (<see cref="CleanupStepActivity"/>) so a mid-sequence failure no
///     longer aborts the run (item #3).</description></item>
///   <item><description>A single terminal activity
///     (<see cref="EmitDeleteTerminalEventActivity"/>) is last — exactly one
///     terminal event per run.</description></item>
///   <item><description>Ordering: evict precedes drop-schema; backup
///     precedes drop-schema; drop-schema precedes drop-role; CP relationship
///     cleanup precedes the terminal.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class DeleteTenantWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
        typeof(Event),                                  // item #1 — starter trigger
        typeof(SetVariable),                            // initInputs
        typeof(MarkTenantDeletingForDeleteActivity),
        typeof(EvictTenantPoolForCleanupActivity),
        typeof(BackupTenantDatabaseForDeleteActivity),  // AC4 — pre-drop backup (gated)
        typeof(GuardTenantDeletingActivity),            // cancellation guard — last check before drop
        typeof(DropTenantSchemaForCleanupActivity),
        typeof(DropTenantRoleForCleanupActivity),
        typeof(CleanupTenantRelationshipsActivity),     // item #4 — CP relationship cleanup
        typeof(EmitDeleteTerminalEventActivity),        // item #3 — single terminal
    };

    [Test]
    public void Build_PopulatesMetadata()
    {
        var workflow = new DeleteTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be("delete-tenant");
        builder.Object.Name.Should().Be("Delete Tenant");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
        builder.Object.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_RootIsSequenceWithExpectedSteps()
    {
        var workflow = new DeleteTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.Root.Should().BeOfType<Sequence>();
        var sequence = (Sequence)builder.Object.Root;
        var activities = sequence.Activities.ToList();
        activities.Should().HaveCount(ExpectedActivitiesInOrder.Length);

        for (var i = 0; i < ExpectedActivitiesInOrder.Length; i++)
        {
            activities[i].GetType()
                .Should()
                .Be(ExpectedActivitiesInOrder[i],
                    $"position {i} should be {ExpectedActivitiesInOrder[i].Name}");
        }
    }

    [Test]
    public void Build_StarterTrigger_BindsToDeleteRequestedEventName()
    {
        var workflow = new DeleteTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var sequence = (Sequence)builder.Object.Root;
        var trigger = sequence.Activities.OfType<Event>().FirstOrDefault();

        trigger.Should().NotBeNull("the workflow must start with an Event trigger (item #1)");
        trigger!.Id.Should().Be("OnDeleteRequested");
        var raw = trigger.EventName.Expression?.Value?.ToString();
        raw.Should().Be(DeleteTenantWorkflow.DeleteRequestedEventName);
    }

    [Test]
    public void Build_EvictPoolPrecedesDropSchema()
    {
        var sequence = SequenceOf();
        var evictIdx = sequence.FindIndex(a => a is EvictTenantPoolForCleanupActivity);
        var dropDbIdx = sequence.FindIndex(a => a is DropTenantSchemaForCleanupActivity);

        evictIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeGreaterThan(0);
        evictIdx.Should().BeLessThan(dropDbIdx,
            "the resolver pool must be evicted before DROP SCHEMA … CASCADE");
    }

    [Test]
    public void Build_BackupPrecedesDropSchema()
    {
        var sequence = SequenceOf();
        var backupIdx = sequence.FindIndex(a => a is BackupTenantDatabaseForDeleteActivity);
        var dropDbIdx = sequence.FindIndex(a => a is DropTenantSchemaForCleanupActivity);

        backupIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeGreaterThan(0);
        backupIdx.Should().BeLessThan(dropDbIdx,
            "AC4 — the pg_dump backup must complete before DROP SCHEMA");
    }

    [Test]
    public void Build_CancellationGuardImmediatelyPrecedesDropSchema()
    {
        // CRITICAL (cancellation race) — the guard re-reads Status as the LAST
        // act before the irreversible DROP SCHEMA, so a cancel that lands after
        // dispatch aborts the run before anything is dropped.
        var sequence = SequenceOf();
        var guardIdx = sequence.FindIndex(a => a is GuardTenantDeletingActivity);
        var dropDbIdx = sequence.FindIndex(a => a is DropTenantSchemaForCleanupActivity);

        guardIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().Be(guardIdx + 1,
            "the cancellation guard must run immediately before DROP SCHEMA … CASCADE");
    }

    [Test]
    public void Build_BackupPrecedesCancellationGuard()
    {
        // The guard is the LAST step before the drop; the (gated) backup runs
        // before it so a backup is still taken on the non-aborted path.
        var sequence = SequenceOf();
        var backupIdx = sequence.FindIndex(a => a is BackupTenantDatabaseForDeleteActivity);
        var guardIdx = sequence.FindIndex(a => a is GuardTenantDeletingActivity);

        backupIdx.Should().BeGreaterThan(0);
        guardIdx.Should().BeGreaterThan(backupIdx);
    }

    [Test]
    public void Build_DropSchemaPrecedesDropRole()
    {
        var sequence = SequenceOf();
        var dropDbIdx = sequence.FindIndex(a => a is DropTenantSchemaForCleanupActivity);
        var dropRoleIdx = sequence.FindIndex(a => a is DropTenantRoleForCleanupActivity);

        dropDbIdx.Should().BeGreaterThan(0);
        dropRoleIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeLessThan(dropRoleIdx,
            "DROP ROLE fails if the role still owns the schema");
    }

    [Test]
    public void Build_RelationshipCleanupPrecedesTerminal()
    {
        var sequence = SequenceOf();
        var relIdx = sequence.FindIndex(a => a is CleanupTenantRelationshipsActivity);
        var terminalIdx = sequence.FindIndex(a => a is EmitDeleteTerminalEventActivity);

        relIdx.Should().BeGreaterThan(0);
        terminalIdx.Should().Be(relIdx + 1,
            "the CP relationship cleanup must run immediately before the terminal so a "
            + "dangling-FK failure is attributed before the soft-delete decision");
    }

    [Test]
    public void Build_TerminalActivityIsLastInSequence()
    {
        var sequence = SequenceOf();
        sequence.Last().Should().BeOfType<EmitDeleteTerminalEventActivity>(
            "the single terminal event must fire after every per-step activity has run");
    }

    [Test]
    public void Build_AllDestructiveStepsAreContinueOnError()
    {
        // Item #3 — every per-step activity inherits CleanupStepActivity
        // (catch-record-continue). None inherits the throwing
        // TenantLifecycleActivity base — that's what guarantees the run
        // always reaches the terminal step.
        var sequence = SequenceOf();
        var stepActivities = sequence
            .Where(a => a is not SetVariable and not EmitDeleteTerminalEventActivity and not Event)
            .ToList();

        stepActivities.Should().HaveCount(7);
        foreach (var step in stepActivities)
        {
            step.Should().BeAssignableTo<CleanupStepActivity>(
                $"{step.GetType().Name} must inherit CleanupStepActivity for continue-on-error semantics");
        }
    }

    [Test]
    public void Build_TerminalActivity_DoesNotInheritStepBase()
    {
        // The terminal emits the SINGLE terminal event, not the per-step
        // tuple — locking the inheritance guards the single-terminal-event
        // invariant.
        new EmitDeleteTerminalEventActivity().Should()
            .NotBeAssignableTo<CleanupStepActivity>();
    }

    private static List<IActivity> SequenceOf()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeleteTenantWorkflow());
        return ((Sequence)builder.Object.Root).Activities.ToList();
    }
}
