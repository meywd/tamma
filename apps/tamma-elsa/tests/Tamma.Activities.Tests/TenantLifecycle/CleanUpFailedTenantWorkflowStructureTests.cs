using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Tests.Workflows;
using Tamma.Activities.TenantLifecycle;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// H6 / Story 28-5 AC7 — structural assertions on
/// <see cref="CleanUpFailedTenantWorkflow"/>: the workflow is a
/// <see cref="Sequence"/> of one input-binding step, four sibling
/// continue-on-error cleanup activities, and one terminal activity.
///
/// <para><b>Round-2 review fix (H6)</b>: the previous shape was a
/// single composite <c>CleanUpFailedTenantActivity</c> wrapping a
/// hand-rolled mini-orchestrator. The new shape decomposes into
/// per-step activities so Elsa can suspend / replay / cancel between
/// steps.</para>
/// </summary>
[TestFixture]
public class CleanUpFailedTenantWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
        typeof(SetVariable),                              // initInputs
        typeof(EvictTenantPoolForCleanupActivity),        // step 1
        typeof(DropTenantDatabaseForCleanupActivity),     // step 2
        typeof(DropTenantRoleForCleanupActivity),         // step 3
        typeof(SoftDeleteTenantRowActivity),              // step 4
        typeof(EmitCleanupTerminalEventActivity),         // terminal
    };

    [Test]
    public void Build_PopulatesMetadata()
    {
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be("clean-up-failed-tenant",
            "the API endpoint that publishes TENANT.CLEANUP.REQUESTED binds on this id — DO NOT rename");
        builder.Object.Name.Should().Be("Clean Up Failed Tenant");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
        builder.Object.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_RootIsSequenceWithExpectedSteps()
    {
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.Root.Should().BeOfType<Sequence>();
        var sequence = (Sequence)builder.Object.Root!;
        var activities = sequence.Activities.ToList();
        activities.Should().HaveCount(ExpectedActivitiesInOrder.Length);
        for (var i = 0; i < ExpectedActivitiesInOrder.Length; i++)
        {
            activities[i].Should().BeOfType(ExpectedActivitiesInOrder[i],
                $"Position {i} must be {ExpectedActivitiesInOrder[i].Name}");
        }
    }

    [Test]
    public void Build_EvictPoolPrecedesDropDatabase()
    {
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root!;
        var activities = sequence.Activities.ToList();

        var evictIdx = activities.FindIndex(a => a is EvictTenantPoolForCleanupActivity);
        var dropDbIdx = activities.FindIndex(a => a is DropTenantDatabaseForCleanupActivity);

        evictIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeGreaterThan(0);
        evictIdx.Should().BeLessThan(dropDbIdx,
            "the resolver pool must be evicted before DROP DATABASE WITH (FORCE) "
            + "so the cached NpgsqlDataSource is released first");
    }

    [Test]
    public void Build_DropDatabasePrecedesDropRole()
    {
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root!;
        var activities = sequence.Activities.ToList();

        var dropDbIdx = activities.FindIndex(a => a is DropTenantDatabaseForCleanupActivity);
        var dropRoleIdx = activities.FindIndex(a => a is DropTenantRoleForCleanupActivity);

        dropDbIdx.Should().BeGreaterThan(0);
        dropRoleIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeLessThan(dropRoleIdx,
            "DROP OWNED BY in DropTenantRoleForCleanupActivity fails if the role still owns the DB");
    }

    [Test]
    public void Build_TerminalActivityIsLastInSequence()
    {
        // Story 28-5 single-terminal-event invariant: only
        // EmitCleanupTerminalEventActivity emits a terminal event, and
        // it must run AFTER every per-step activity has had a chance
        // to record success/failure into the workflow accumulator.
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root!;
        var activities = sequence.Activities.ToList();

        activities.Last().Should().BeOfType<EmitCleanupTerminalEventActivity>(
            "the terminal event must fire after every per-step activity has run");
    }

    [Test]
    public void Build_SoftDeleteRowImmediatelyPrecedesTerminal()
    {
        // Sanity check: the soft-delete is the last "work" step,
        // followed by the terminal event. Anything between them would
        // open a window where the terminal event reports success but
        // the row isn't yet in the deleted state.
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root!;
        var activities = sequence.Activities.ToList();

        var softDeleteIdx = activities.FindIndex(a => a is SoftDeleteTenantRowActivity);
        var terminalIdx = activities.FindIndex(a => a is EmitCleanupTerminalEventActivity);
        softDeleteIdx.Should().BeGreaterThan(0);
        terminalIdx.Should().Be(softDeleteIdx + 1,
            "the terminal event must immediately follow the soft-delete step");
    }

    [Test]
    public void Build_AllStepActivitiesAreContinueOnError()
    {
        // Defining-feature assertion for H6: every per-step activity
        // in the cleanup Sequence inherits from CleanupStepActivity
        // (which catches internally) — none of them inherits from
        // TenantLifecycleActivity (which throws on failure). This is
        // what gives the workflow continue-on-error semantics step by
        // step.
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root!;
        var stepActivities = sequence.Activities
            .Where(a => a is not SetVariable and not EmitCleanupTerminalEventActivity)
            .ToList();

        stepActivities.Should().HaveCount(4);
        foreach (var step in stepActivities)
        {
            step.Should().BeAssignableTo<CleanupStepActivity>(
                $"{step.GetType().Name} must inherit from CleanupStepActivity for continue-on-error semantics");
        }
    }
}
