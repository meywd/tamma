using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 items #3 + #6 — wiring assertions for the delete-workflow
/// continue-on-error step activities and the <c>StepEventFamily</c> hook
/// that switches delete steps to the <c>TENANT.DELETE.STEP_*</c> prefix.
/// </summary>
[TestFixture]
public class DeleteStepActivityTests
{
    [Test]
    public void DeleteSteps_AreContinueOnError_WithDistinctStepNames()
    {
        var mark = new MarkTenantDeletingForDeleteActivity();
        var guard = new GuardTenantDeletingActivity();
        var backup = new BackupTenantDatabaseForDeleteActivity();
        var relationships = new CleanupTenantRelationshipsActivity();

        mark.Should().BeAssignableTo<CleanupStepActivity>();
        guard.Should().BeAssignableTo<CleanupStepActivity>();
        backup.Should().BeAssignableTo<CleanupStepActivity>();
        relationships.Should().BeAssignableTo<CleanupStepActivity>();

        mark.StepName.Should().Be(CleanupSteps.MarkDeleting);
        guard.StepName.Should().Be(CleanupSteps.GuardDeleting);
        backup.StepName.Should().Be(CleanupSteps.BackupDatabase);
        relationships.StepName.Should().Be(CleanupSteps.CleanupRelationships);

        new[] { mark.StepName, guard.StepName, backup.StepName, relationships.StepName }
            .Distinct().Should().HaveCount(4);
    }

    [Test]
    public void StepEventFamily_Delete_ResolvesToDeletePrefix()
    {
        // Item #6 — delete steps emit TENANT.DELETE.STEP_*, not PROVISION.*.
        var (started, completed, failed) =
            TenantLifecycleActivity.StepEventNamesFor(TenantStepEventFamily.Delete);

        started.Should().Be("TENANT.DELETE.STEP_STARTED");
        completed.Should().Be("TENANT.DELETE.STEP_COMPLETED");
        failed.Should().Be("TENANT.DELETE.STEP_FAILED");
    }

    [Test]
    public void StepEventFamily_Provision_ResolvesToProvisionPrefix()
    {
        var (started, completed, failed) =
            TenantLifecycleActivity.StepEventNamesFor(TenantStepEventFamily.Provision);

        started.Should().Be("TENANT.PROVISION.STEP_STARTED");
        completed.Should().Be("TENANT.PROVISION.STEP_COMPLETED");
        failed.Should().Be("TENANT.PROVISION.STEP_FAILED");
    }

    [Test]
    public void DeleteFailedEventConstant_MatchesLiteral()
    {
        // The cleanup terminal emits this literal; centralising the constant
        // keeps delete + cleanup in lockstep.
        TenantLifecycleEvents.DeleteFailed.Should().Be("TENANT.DELETE.FAILED");
    }
}
