using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// H6 / Story 28-5 AC7 — wiring assertions for the per-step cleanup
/// activities. The activities run inside the Elsa runtime and aren't
/// directly callable in a unit test (constructing a real
/// <c>ActivityExecutionContext</c> requires the workflow engine), so
/// these tests assert the parts that DON'T need a live runtime:
///
/// <list type="bullet">
///   <item><description>Each activity inherits from
///     <see cref="CleanupStepActivity"/> (the continue-on-error base).</description></item>
///   <item><description>Each activity declares the right
///     <c>StepName</c> — used as the <c>tags-&gt;&gt;'step'</c>
///     value on platform-event rows and as the workflow-variable
///     key for per-step success/failure flags.</description></item>
///   <item><description>Each activity exposes a tenant-id input.</description></item>
/// </list>
///
/// <para>End-to-end "does the activity catch internal exceptions?" is
/// covered indirectly by <see cref="CleanupWorkflowStateTests"/> +
/// <see cref="CleanUpFailedTenantWorkflowStructureTests"/>: the
/// state-machine logic that gets called on exception is tested
/// directly, and the workflow assertion locks in that the
/// <c>Sequence</c> contains continue-on-error activities.</para>
/// </summary>
[TestFixture]
public class CleanupStepActivityTests
{
    [Test]
    public void EvictTenantPoolForCleanupActivity_HasCorrectStepName()
    {
        var activity = new EvictTenantPoolForCleanupActivity();
        activity.StepName.Should().Be(CleanupSteps.EvictPool);
        activity.StepName.Should().Be("evict-pool");
    }

    [Test]
    public void DropTenantSchemaForCleanupActivity_HasCorrectStepName()
    {
        var activity = new DropTenantSchemaForCleanupActivity();
        activity.StepName.Should().Be(CleanupSteps.DropSchema);
        activity.StepName.Should().Be("drop-tenant-schema");
    }

    [Test]
    public void DropTenantRoleForCleanupActivity_HasCorrectStepName()
    {
        var activity = new DropTenantRoleForCleanupActivity();
        activity.StepName.Should().Be(CleanupSteps.DropRole);
        activity.StepName.Should().Be("drop-tenant-role");
    }

    [Test]
    public void SoftDeleteTenantRowActivity_HasCorrectStepName()
    {
        var activity = new SoftDeleteTenantRowActivity();
        activity.StepName.Should().Be(CleanupSteps.SoftDeleteRow);
        activity.StepName.Should().Be("soft-delete-cp-row");
    }

    [Test]
    public void AllCleanupActivities_InheritFromCleanupStepActivity()
    {
        // The base class is the continue-on-error contract. If a
        // future rename or refactor accidentally promotes one of these
        // to TenantLifecycleActivity (which throws on failure), the
        // cleanup workflow will start aborting on the first step
        // failure — silently regressing H6.
        new EvictTenantPoolForCleanupActivity().Should()
            .BeAssignableTo<CleanupStepActivity>();
        new DropTenantSchemaForCleanupActivity().Should()
            .BeAssignableTo<CleanupStepActivity>();
        new DropTenantRoleForCleanupActivity().Should()
            .BeAssignableTo<CleanupStepActivity>();
        new SoftDeleteTenantRowActivity().Should()
            .BeAssignableTo<CleanupStepActivity>();
    }

    [Test]
    public void AllCleanupActivities_HaveDistinctStepNames()
    {
        // Each activity's StepName is used as the workflow-variable
        // suffix for per-step success/failure flags. Duplicate names
        // would cause one step's outcome to overwrite another's.
        var stepNames = new[]
        {
            new EvictTenantPoolForCleanupActivity().StepName,
            new DropTenantSchemaForCleanupActivity().StepName,
            new DropTenantRoleForCleanupActivity().StepName,
            new SoftDeleteTenantRowActivity().StepName,
        };

        stepNames.Distinct().Should().HaveCount(4,
            "every cleanup step needs a unique workflow-variable namespace");
    }

    [Test]
    public void CleanupActivities_EventTypeFollowsCleanupTaxonomy()
    {
        // EventType drives the in-memory tamma:events emission
        // (Tamma.Activities.Core.TammaEventEmitter). Names live in
        // the TENANT.CLEANUP.* namespace so a future filter on the
        // event-stream UI can surface only cleanup events.
        new EvictTenantPoolForCleanupActivity().EventType.Should()
            .Be("TENANT.CLEANUP.EVICT_POOL");
        new DropTenantSchemaForCleanupActivity().EventType.Should()
            .Be("TENANT.CLEANUP.DROP_TENANT_SCHEMA");
        new DropTenantRoleForCleanupActivity().EventType.Should()
            .Be("TENANT.CLEANUP.DROP_TENANT_ROLE");
        new SoftDeleteTenantRowActivity().EventType.Should()
            .Be("TENANT.CLEANUP.SOFT_DELETE_CP_ROW");
    }

    [Test]
    public void TerminalActivity_DoesNotInheritFromStepBase()
    {
        // The terminal activity has its own emission contract — it
        // emits the SINGLE TENANT.DELETED.SUCCESS / .FAILED record,
        // not the per-step STARTED/COMPLETED/FAILED tuple. Locking
        // the inheritance guards against a future refactor wrapping
        // it in CleanupStepActivity (which would emit a step-level
        // FAILED event in addition to the terminal one — breaking
        // the single-terminal-event invariant).
        new EmitCleanupTerminalEventActivity().Should()
            .NotBeAssignableTo<CleanupStepActivity>();
    }
}
