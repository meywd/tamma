using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1, 2026-07-30 review Finding 1.3 — the <c>applied</c> field on
/// <c>GET /api/admin/tenants/migrate/{runId}</c>.
///
/// <para>The defect: <c>applied</c> was a boolean computed as
/// <c>!dryRun &amp;&amp; state != failed</c>, so it reported <c>true</c> for a run
/// still <c>running</c> that had not touched a single tenant, and <c>false</c> —
/// on a field whose stated purpose is to say "nothing was written" — after a
/// partial failure that may have migrated most of the fleet, with
/// <c>result: null</c> so the operator could not even tell WHICH tenants got the
/// DDL. Backwards from the field's own contract, at the worst possible moment.</para>
///
/// <para>The invariant these tests pin: <c>not-applied</c> is the only value
/// that carries a guarantee, and it is only ever used where one exists. No
/// Docker — this is pure projection over a run record.</para>
/// </summary>
[TestFixture]
public class AdminTenantMigrationRunResponseTests
{
    private static TenantMigrationSweepRun Run(
        string state,
        bool dryRun,
        TenantMigrationSweepResult? result = null,
        bool partial = false) =>
        new(
            Guid.NewGuid(),
            state,
            dryRun,
            MaxConcurrency: 4,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: state == TenantMigrationSweepRunState.Running ? null : DateTimeOffset.UtcNow,
            Error: state == TenantMigrationSweepRunState.Failed ? "boom" : null,
            Result: result,
            ResultIsPartial: partial);

    private static TenantMigrationSweepResult Result(int migrated, int failed = 0) =>
        TenantMigrationSweep.Summarize(
            dryRun: false,
            Enumerable.Range(0, migrated)
                .Select(_ => new TenantMigrationSweepEntry(
                    Guid.NewGuid(), TenantMigrationSweep.OutcomeMigrated, 1, null))
                .Concat(Enumerable.Range(0, failed)
                    .Select(_ => new TenantMigrationSweepEntry(
                        Guid.NewGuid(), TenantMigrationSweep.OutcomeFailed, 0, "x")))
                .ToList());

    [Test]
    public void A_running_apply_is_not_reported_as_applied()
    {
        var response = AdminTenantMigrationRunResponse.From(
            Run(TenantMigrationSweepRunState.Running, dryRun: false));

        response.Applied.Should().Be(AdminTenantMigrationApplied.Partial,
            "the old boolean said applied=true here — while still running, before a single "
            + "tenant had been touched");
        response.Mode.Should().Be(AdminTenantMigrationMode.Apply,
            "INTENT still lives in `mode`; `applied` is about what is known to be written");
    }

    [Test]
    public void A_failed_apply_that_migrated_tenants_is_not_reported_as_not_applied()
    {
        var response = AdminTenantMigrationRunResponse.From(
            Run(TenantMigrationSweepRunState.Failed, dryRun: false, Result(migrated: 7), partial: true));

        response.Applied.Should().Be(AdminTenantMigrationApplied.Partial,
            "the old boolean said applied=false — i.e. 'nothing was written' — for a run "
            + "that had already migrated seven tenants");
        response.ResultIsPartial.Should().BeTrue();
        response.Result!.Migrated.Should().Be(7);
        response.Result.Tenants.Should().HaveCount(7,
            "which tenants got the DDL is the question a failed fleet-DDL run must answer");
        response.Result.Message.Should().Contain("PARTIAL",
            "the nested body must not read like a complete sweep");
    }

    [Test]
    public void A_failed_apply_that_migrated_nothing_may_say_so()
    {
        // The one case where the tri-state can still make the strong claim: the
        // partial result PROVES zero tenants were migrated.
        var response = AdminTenantMigrationRunResponse.From(
            Run(TenantMigrationSweepRunState.Failed, dryRun: false, Result(migrated: 0), partial: true));

        response.Applied.Should().Be(AdminTenantMigrationApplied.No);
    }

    [Test]
    public void A_failed_apply_with_no_result_at_all_stays_pessimistic()
    {
        var response = AdminTenantMigrationRunResponse.From(
            Run(TenantMigrationSweepRunState.Failed, dryRun: false));

        response.Applied.Should().Be(AdminTenantMigrationApplied.Partial,
            "with nothing to prove otherwise, the honest answer is 'some tenants may carry "
            + "the DDL' — never the guarantee");
    }

    [Test]
    public void A_completed_apply_is_applied()
    {
        var response = AdminTenantMigrationRunResponse.From(
            Run(TenantMigrationSweepRunState.Completed, dryRun: false, Result(migrated: 3, failed: 1)));

        response.Applied.Should().Be(AdminTenantMigrationApplied.Yes);
        response.ResultIsPartial.Should().BeFalse();
        response.Result!.Message.Should().NotContain("PARTIAL");
    }

    [TestCase(TenantMigrationSweepRunState.Running)]
    [TestCase(TenantMigrationSweepRunState.Completed)]
    [TestCase(TenantMigrationSweepRunState.Failed)]
    public void A_dry_run_is_never_anything_but_not_applied(string state)
    {
        var response = AdminTenantMigrationRunResponse.From(Run(state, dryRun: true));

        response.Applied.Should().Be(AdminTenantMigrationApplied.No,
            "a dry run writes nothing by construction, in every state it can reach");
    }
}
