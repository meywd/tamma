using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic41;

/// <summary>
/// Story 41-30 — Testcontainers coverage for
/// <see cref="ScheduledTriggerRepository"/> against REAL Postgres (AC1, AC2):
/// the <c>ON CONFLICT DO NOTHING</c> claim race is the headline. The shared
/// <see cref="ApiTestFixture"/> already migrated the control-plane container,
/// so <c>scheduled_triggers</c> / <c>scheduled_trigger_fires</c> physically
/// exist (which also round-trips the idempotent raw-SQL migration).
/// </summary>
[TestFixture]
public class ScheduledTriggerRepositoryTests
{
    /// <summary>Minimal factory over the fixture's CP container — each call
    /// opens a fresh context/connection, exactly like production's pooled
    /// factory from the repository's point of view.</summary>
    private sealed class TestDbFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseNpgsql(ApiTestFixture.Postgres.GetConnectionString())
                .Options;
            return new ControlPlaneDbContext(options);
        }
    }

    private ScheduledTriggerRepository _repository = null!;
    private TestDbFactory _dbFactory = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _dbFactory = new TestDbFactory();
        _repository = new ScheduledTriggerRepository(_dbFactory);
    }

    private async Task<Guid> SeedTenantAsync(bool softDeleted = false)
    {
        await using var db = _dbFactory.CreateDbContext();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "t",
            Slug = $"slug-{Guid.NewGuid():N}",
            Type = "personal",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = softDeleted ? DateTime.UtcNow : null,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<ScheduledTrigger> SeedTriggerAsync(
        Guid? tenantId, string definitionId = "test-noop-definition",
        string name = "nightly-audit", bool enabled = true)
    {
        await using var db = _dbFactory.CreateDbContext();
        var trigger = new ScheduledTrigger
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DefinitionId = definitionId,
            Name = name,
            CronExpression = "0 * * * *",
            Enabled = enabled,
            InputJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ScheduledTriggers.Add(trigger);
        await db.SaveChangesAsync();
        return trigger;
    }

    private static ScheduledTriggerFire Fire(ScheduledTrigger trigger, string windowKey) => new()
    {
        Id = Guid.NewGuid(),
        TriggerId = trigger.Id,
        TenantId = trigger.TenantId!.Value,
        DefinitionId = trigger.DefinitionId,
        WindowKey = windowKey,
        ClaimedAt = DateTime.UtcNow,
    };

    // ── THE HEADLINE (AC1): the claim race ──

    [Test]
    public async Task TryClaimFire_EightConcurrentClaims_SameWindow_ExactlyOneWins()
    {
        var tenant = await SeedTenantAsync();
        var trigger = await SeedTriggerAsync(tenant);
        const string windowKey = "2026-07-27T03:00:00Z";

        // Eight concurrent "pods", each with its own connection, racing the
        // same (trigger, window). Postgres's unique index arbitrates.
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _repository.TryClaimFireAsync(Fire(trigger, windowKey)))));

        results.Count(r => r).Should().Be(1,
            "AC1 — at most one pod may own a (trigger, window) across the fleet");

        await using var db = _dbFactory.CreateDbContext();
        (await db.ScheduledTriggerFires.CountAsync(
            f => f.TriggerId == trigger.Id && f.WindowKey == windowKey))
            .Should().Be(1);
    }

    [Test]
    public async Task TryClaimFire_AfterTheWinnerCommitted_AThirdClaim_StillLoses_TheSequentialDoubleFireCase()
    {
        var tenant = await SeedTenantAsync();
        var trigger = await SeedTriggerAsync(tenant);
        const string windowKey = "2026-07-27T03:00:00Z";

        (await _repository.TryClaimFireAsync(Fire(trigger, windowKey))).Should().BeTrue();

        // Correction 3 — the crash case: a pod that died released its
        // SESSION-scoped advisory lock, but the COMMITTED ledger row is what
        // stops the next pod's (or the restarted pod's) re-claim. Fresh
        // repository instance = fresh connections = "another process".
        var restartedPod = new ScheduledTriggerRepository(_dbFactory);
        (await restartedPod.TryClaimFireAsync(Fire(trigger, windowKey))).Should().BeFalse(
            "only a committed ledger row prevents sequential double-fire after a pod crash");
    }

    [Test]
    public async Task TryClaimFire_ADifferentTenantsTrigger_OnTheSameWindow_Succeeds()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var triggerA = await SeedTriggerAsync(tenantA);
        var triggerB = await SeedTriggerAsync(tenantB);
        const string windowKey = "2026-07-27T03:00:00Z";

        (await _repository.TryClaimFireAsync(Fire(triggerA, windowKey))).Should().BeTrue();
        (await _repository.TryClaimFireAsync(Fire(triggerB, windowKey))).Should().BeTrue(
            "AC2 — tenant A's claim must never suppress tenant B's for the same window");
    }

    // ── outcome stamping round-trip ──

    [Test]
    public async Task StampOutcome_RoundTrips_Dispatched_And_Failed()
    {
        var tenant = await SeedTenantAsync();
        var trigger = await SeedTriggerAsync(tenant);
        var fire = Fire(trigger, "2026-07-27T03:00:00Z");
        (await _repository.TryClaimFireAsync(fire)).Should().BeTrue();

        var dispatchedAt = DateTime.UtcNow;
        await _repository.StampOutcomeAsync(fire.Id, "dispatched", "instance-1", null, dispatchedAt);

        await using (var db = _dbFactory.CreateDbContext())
        {
            var row = await db.ScheduledTriggerFires.SingleAsync(f => f.Id == fire.Id);
            row.Outcome.Should().Be("dispatched");
            row.WorkflowInstanceId.Should().Be("instance-1");
            row.DispatchedAt.Should().BeCloseTo(dispatchedAt, TimeSpan.FromSeconds(1));
        }

        await _repository.StampOutcomeAsync(fire.Id, "failed", null, "engine gone", null);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var row = await db.ScheduledTriggerFires.SingleAsync(f => f.Id == fire.Id);
            row.Outcome.Should().Be("failed");
            row.Detail.Should().Be("engine gone");
        }
    }

    // ── template materialisation (D6) ──

    [Test]
    public async Task MaterialiseTemplates_CreatesOneConcreteRowPerActiveTenant_Idempotently_SkippingSoftDeleted()
    {
        var active1 = await SeedTenantAsync();
        var active2 = await SeedTenantAsync();
        await SeedTenantAsync(softDeleted: true);
        await SeedTriggerAsync(tenantId: null); // the platform template

        var activeTenants = await _repository.SnapshotActiveTenantIdsAsync();
        activeTenants.Should().BeEquivalentTo(new[] { active1, active2 },
            "the snapshot excludes soft-deleted tenants");

        (await _repository.MaterialiseTemplatesAsync(activeTenants, DateTime.UtcNow))
            .Should().Be(2, "one concrete row per active tenant");
        (await _repository.MaterialiseTemplatesAsync(activeTenants, DateTime.UtcNow))
            .Should().Be(0, "materialisation is idempotent (ON CONFLICT DO NOTHING)");

        var concrete = await _repository.ListEnabledConcreteTriggersAsync(activeTenants);
        concrete.Should().HaveCount(2);
        concrete.Select(t => t.TenantId).Should().BeEquivalentTo(new Guid?[] { active1, active2 });
        concrete.Should().OnlyContain(t => t.DefinitionId == "test-noop-definition");
    }

    [Test]
    public async Task ListEnabledConcreteTriggers_NeverReturns_Templates_Or_DisabledRows()
    {
        var tenant = await SeedTenantAsync();
        await SeedTriggerAsync(tenantId: null, name: "template-only");
        await SeedTriggerAsync(tenant, name: "disabled-row", enabled: false);
        var enabled = await SeedTriggerAsync(tenant, name: "enabled-row");

        var rows = await _repository.ListEnabledConcreteTriggersAsync(new[] { tenant });

        rows.Should().ContainSingle(t => t.Id == enabled.Id,
            "templates are materialised, never fired (D6); disabled rows never fire");
    }

    // ── ledger retention (D2) ──

    [Test]
    public async Task PruneLedger_Deletes_OnlyRowsOlderThanTheCutoff_Bounded()
    {
        var tenant = await SeedTenantAsync();
        var trigger = await SeedTriggerAsync(tenant);
        var now = DateTime.UtcNow;

        await using (var db = _dbFactory.CreateDbContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.ScheduledTriggerFires.Add(new ScheduledTriggerFire
                {
                    Id = Guid.NewGuid(),
                    TriggerId = trigger.Id,
                    TenantId = tenant,
                    DefinitionId = trigger.DefinitionId,
                    WindowKey = $"2026-01-01T0{i}:00:00Z",
                    ClaimedAt = now.AddDays(-100 - i),
                    Outcome = "dispatched",
                });
            }
            db.ScheduledTriggerFires.Add(new ScheduledTriggerFire
            {
                Id = Guid.NewGuid(),
                TriggerId = trigger.Id,
                TenantId = tenant,
                DefinitionId = trigger.DefinitionId,
                WindowKey = "2026-07-27T03:00:00Z",
                ClaimedAt = now,
                Outcome = "dispatched",
            });
            await db.SaveChangesAsync();
        }

        (await _repository.PruneLedgerAsync(now.AddDays(-90), maxRows: 3))
            .Should().Be(3, "the per-tick DELETE is bounded");
        (await _repository.PruneLedgerAsync(now.AddDays(-90), maxRows: 100))
            .Should().Be(2, "the next tick takes the rest");

        await using var verify = _dbFactory.CreateDbContext();
        var remaining = await verify.ScheduledTriggerFires.SingleAsync();
        remaining.WindowKey.Should().Be("2026-07-27T03:00:00Z",
            "recent ledger rows — the live at-most-once evidence — survive pruning");
    }

    // ── the manual run-now drain feed (D8) ──

    [Test]
    public async Task ListPendingManualFires_Returns_OnlyUndispatched_ManualClaims_WithTheirTriggers()
    {
        var tenant = await SeedTenantAsync();
        var trigger = await SeedTriggerAsync(tenant);
        var manual = Fire(trigger, "manual:20260727T121500.000Z");
        var cron = Fire(trigger, "2026-07-27T12:00:00Z");
        (await _repository.TryClaimFireAsync(manual)).Should().BeTrue();
        (await _repository.TryClaimFireAsync(cron)).Should().BeTrue();

        var pending = await _repository.ListPendingManualFiresAsync(10);
        pending.Should().HaveCount(1, "cron claims are the tick's own path, not the manual drain's");
        pending[0].Fire.WindowKey.Should().Be(manual.WindowKey);
        pending[0].Trigger.Id.Should().Be(trigger.Id);

        await _repository.StampOutcomeAsync(manual.Id, "dispatched", "i-1", null, DateTime.UtcNow);
        (await _repository.ListPendingManualFiresAsync(10)).Should().BeEmpty();
    }
}
