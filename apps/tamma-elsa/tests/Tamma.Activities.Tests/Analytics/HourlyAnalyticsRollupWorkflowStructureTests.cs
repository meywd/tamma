using Elsa.Workflows;
using Elsa.Workflows.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Analytics;
using Tamma.Activities.Tests.Workflows;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 28-10 — structural assertions on
/// <see cref="HourlyAnalyticsRollupWorkflow"/>. The workflow has to
/// land in a very specific shape (linear Sequence, platform rollup
/// first, per-tenant fan-out second, terminal emit last) because the
/// cron trigger cannot tolerate partial-state restarts that skip the
/// platform row.
/// </summary>
[TestFixture]
public class HourlyAnalyticsRollupWorkflowStructureTests
{
    private static readonly Type[] ExpectedActivitiesInOrder = new[]
    {
        typeof(SetVariable),                         // initBucket
        typeof(ComputePlatformRollupActivity),
        typeof(FanOutTenantRollupsActivity),         // platform fact table (28-10)
        typeof(FanOutTenantDimensionalRollupsActivity), // per-tenant dimensional (36-2)
        typeof(EmitHourCompletedActivity),
        typeof(PurgeStaleAnalyticsActivity),         // PURGE_ANALYTICS_HOURLY (CP, 28-10)
    };

    [Test]
    public void Build_PopulatesMetadata()
    {
        var workflow = new HourlyAnalyticsRollupWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be("hourly-analytics-rollup");
        builder.Object.Name.Should().Be("Hourly Analytics Rollup");
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
        builder.Object.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_RootIsSequenceWithExpectedSteps()
    {
        var workflow = new HourlyAnalyticsRollupWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.Root.Should().BeOfType<Sequence>(
            "the hourly rollup is intentionally linear");

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
    public void Build_DimensionalFanOut_FollowsPlatformFanOut()
    {
        // Story 36-2 AC14 — the dimensional fan-out is an additional step AFTER
        // the platform fact-table fan-out and before the terminal emit/purge.
        var workflow = new HourlyAnalyticsRollupWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var activities = ((Sequence)builder.Object.Root).Activities.ToList();

        var platformIdx = activities.FindIndex(a => a is FanOutTenantRollupsActivity);
        var dimensionalIdx = activities.FindIndex(a => a is FanOutTenantDimensionalRollupsActivity);
        var emitIdx = activities.FindIndex(a => a is EmitHourCompletedActivity);

        platformIdx.Should().BeGreaterThanOrEqualTo(0);
        dimensionalIdx.Should().BeGreaterThan(platformIdx, "the platform fan-out must precede the dimensional one");
        emitIdx.Should().BeGreaterThan(dimensionalIdx, "EmitHourCompleted stays terminal-before-purge, after the dimensional fan-out");
    }

    [Test]
    public void Build_DeclaresExpectedVariables()
    {
        var workflow = new HourlyAnalyticsRollupWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var names = builder.Object.Variables.Select(v => v.Name).ToHashSet();
        names.Should().Contain(new[] { "TargetHour", "TenantsSuccess", "TenantsFailed" });
    }

    [Test]
    public void CronExpression_IsHourlyAtMinuteFive()
    {
        HourlyAnalyticsRollupWorkflow.CronExpression.Should().Be("0 5 * * * *");
    }

    [Test]
    public void DefinitionId_IsStable()
    {
        // The cron scheduler references this id — accidental renames
        // break the scheduled trigger. Guard with a constant value.
        HourlyAnalyticsRollupWorkflow.DefinitionId.Should().Be("hourly-analytics-rollup");
    }
}
