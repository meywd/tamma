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
/// <see cref="DeleteTenantWorkflow"/>:
///
/// <list type="bullet">
///   <item><description>Build()'s metadata is set.</description></item>
///   <item><description>Root is a Sequence — the delete flow is linear and
///     idempotent.</description></item>
///   <item><description>Steps run in the right order: mark → evict pool →
///     drop DB → drop role → emit success. The pool eviction must precede
///     <c>DROP DATABASE</c> so the resolver releases its cached
///     <c>NpgsqlDataSource</c> before the backends are kicked.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class DeleteTenantWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
        typeof(SetVariable),                       // initInputs
        typeof(MarkTenantDeletingActivity),
        typeof(EvictTenantPoolActivity),
        typeof(BackupTenantDatabaseActivity),      // AC4 — pre-drop backup (gated)
        typeof(DropTenantDatabaseActivity),
        typeof(DropTenantRoleActivity),
        typeof(EmitDeletedSuccessActivity),
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
    public void Build_EvictPoolPrecedesDropDatabase()
    {
        var workflow = new DeleteTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root;
        var activities = sequence.Activities.ToList();

        var evictIdx = -1;
        var dropDbIdx = -1;
        for (var i = 0; i < activities.Count; i++)
        {
            if (activities[i] is EvictTenantPoolActivity) evictIdx = i;
            if (activities[i] is DropTenantDatabaseActivity) dropDbIdx = i;
        }

        evictIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeGreaterThan(0);
        evictIdx.Should().BeLessThan(dropDbIdx,
            "the resolver pool must be evicted before DROP DATABASE WITH (FORCE) "
            + "so the cached NpgsqlDataSource is released first");
    }

    [Test]
    public void Build_BackupPrecedesDropDatabase()
    {
        var workflow = new DeleteTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root;
        var activities = sequence.Activities.ToList();

        var backupIdx = -1;
        var dropDbIdx = -1;
        for (var i = 0; i < activities.Count; i++)
        {
            if (activities[i] is BackupTenantDatabaseActivity) backupIdx = i;
            if (activities[i] is DropTenantDatabaseActivity) dropDbIdx = i;
        }

        backupIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeGreaterThan(0);
        backupIdx.Should().BeLessThan(dropDbIdx,
            "AC4 — the pg_dump backup must complete before DROP DATABASE");
    }

    [Test]
    public void Build_DropDatabasePrecedesDropRole()
    {
        var workflow = new DeleteTenantWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var sequence = (Sequence)builder.Object.Root;
        var activities = sequence.Activities.ToList();

        var dropDbIdx = -1;
        var dropRoleIdx = -1;
        for (var i = 0; i < activities.Count; i++)
        {
            if (activities[i] is DropTenantDatabaseActivity) dropDbIdx = i;
            if (activities[i] is DropTenantRoleActivity) dropRoleIdx = i;
        }

        dropDbIdx.Should().BeGreaterThan(0);
        dropRoleIdx.Should().BeGreaterThan(0);
        dropDbIdx.Should().BeLessThan(dropRoleIdx,
            "DROP OWNED BY in DropTenantRoleActivity fails if the role still owns the DB");
    }
}
