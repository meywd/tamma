using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Tests.Workflows;
using Tamma.Activities.TenantLifecycle;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 — structural assertions on
/// <see cref="CreateTenantWorkflow"/>:
///
/// <list type="bullet">
///   <item><description>Build()'s metadata is set (definitionId / name / version).</description></item>
///   <item><description>Root is a Sequence (not a Flowchart) — the create
///     flow is intentionally linear.</description></item>
///   <item><description>The expected twelve activities are present in
///     order — unified-tenancy Phase 2 inserted
///     <see cref="AssignTenantPlacementActivity"/> +
///     <see cref="CreateTenantSchemaActivity"/> and removed the
///     db-per-tenant CreateTenantDatabaseActivity.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class CreateTenantWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
        typeof(SetVariable),                                       // initInputs
        typeof(MarkProvisioningActivity),
        typeof(AssignTenantPlacementActivity),
        typeof(CreateTenantRoleActivity),
        typeof(CreateTenantSchemaActivity),
        typeof(BuildTenantConnectionStringActivity),
        typeof(MigrateTenantDatabaseActivity),
        typeof(SeedTenantDefaultsActivity),
        typeof(EncryptAndPersistConnectionStringActivity),
        typeof(WarmTenantPoolActivity),
        typeof(MarkTenantActiveActivity),
        typeof(QueueWelcomeEmailActivity),
    };

    [Test]
    public void Build_PopulatesMetadata()
    {
        var workflow = new CreateTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be("create-tenant");
        builder.Object.Name.Should().Be("Create Tenant");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
        builder.Object.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_RootIsSequenceWithExpectedSteps()
    {
        var workflow = new CreateTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.Root.Should().BeOfType<Sequence>(
            "the create flow is intentionally linear");

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
    public void Build_DeclaresExpectedVariables()
    {
        var workflow = new CreateTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var names = builder.Object.Variables.Select(v => v.Name).ToHashSet();
        names.Should().Contain(new[]
        {
            "TenantId", "Attempt", "DatabaseId", "SchemaName",
            "RoleName", "GeneratedPassword", "TenantConnectionString",
        });
        names.Should().NotContain("DatabaseName",
            "Phase 2 removed the db-per-tenant CreateTenantDatabaseActivity");
    }
}
