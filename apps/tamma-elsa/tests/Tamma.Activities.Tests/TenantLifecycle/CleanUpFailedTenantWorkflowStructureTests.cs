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
/// Story 28-5 AC7 — structural assertions on
/// <see cref="CleanUpFailedTenantWorkflow"/>: the workflow starts with
/// the cleanup-event trigger, binds inputs, and runs the composite
/// cleanup activity. Cleanup logic itself lives in the activity for
/// unit-testability without an Elsa runtime.
///
/// <para>Round-2 review M3: the first activity is now an
/// <see cref="Event"/> bound to
/// <see cref="CleanUpFailedTenantWorkflow.CleanupRequestedEventName"/>
/// so the workflow is dispatched when the bridge re-publishes the
/// <c>TENANT.CLEANUP.REQUESTED</c> platform event through
/// <c>IEventPublisher</c>.</para>
/// </summary>
[TestFixture]
public class CleanUpFailedTenantWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
        typeof(Event),                        // round-2 M3 — starter trigger
        typeof(SetVariable),                  // initInputs
        typeof(CleanUpFailedTenantActivity),  // composite cleanup step
    };

    [Test]
    public void Build_PopulatesMetadata()
    {
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be("clean-up-failed-tenant");
        builder.Object.Name.Should().Be("Clean Up Failed Tenant");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
        builder.Object.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_RootIsSequenceWithCompositeStep()
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

    /// <summary>
    /// Round-2 review M3: the starter <see cref="Event"/> must use the
    /// canonical event name so the bridge in <c>Tamma.Api</c> and the
    /// workflow agree on the wire-name. Drift between the constant and
    /// the bridge would silently break cleanup dispatch.
    /// </summary>
    [Test]
    public void Build_StarterTrigger_BindsToCleanupRequestedEventName()
    {
        var workflow = new CleanUpFailedTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var sequence = (Sequence)builder.Object.Root!;
        var trigger = sequence.Activities.OfType<Event>().FirstOrDefault();

        trigger.Should().NotBeNull("the workflow must start with an Event trigger");
        trigger!.Id.Should().Be("OnCleanupRequested");
        // The literal Input<string> wraps a memory-backed value — read
        // via the public EventName property's expression.
        var raw = trigger.EventName.Expression?.Value?.ToString();
        raw.Should().Be(CleanUpFailedTenantWorkflow.CleanupRequestedEventName);
    }
}
