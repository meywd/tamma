using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Tests.Workflows;
using Tamma.Activities.TenantLifecycle;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 AC7 — structural assertions on
/// <see cref="CleanUpFailedTenantWorkflow"/>: the workflow is the
/// thinnest possible wrapper around
/// <see cref="CleanUpFailedTenantActivity"/> (one input-binding step
/// plus the composite cleanup activity). Cleanup logic itself lives in
/// the activity for unit-testability without an Elsa runtime.
/// </summary>
[TestFixture]
public class CleanUpFailedTenantWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
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
}
