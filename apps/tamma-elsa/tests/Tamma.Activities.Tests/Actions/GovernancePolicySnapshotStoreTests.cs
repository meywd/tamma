using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.Tests.LlmCall; // FakeTimeProvider (local test helper)
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Actions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-5 (AC12, amended per the ProviderSettingsStore precedent) — the
/// governance snapshot store: one repository read serves every gate call
/// within the TTL, version-gated installs (a stale load can never clobber a
/// newer one), invalidate-on-write via <c>RefreshAsync</c>, cold-start
/// priming, the per-principal projection (platform rows NEVER appear as
/// principal rows), and the single-user ambient collapse.
/// </summary>
[TestFixture]
public class GovernancePolicySnapshotStoreTests
{
    private sealed class FakeRepository : IActionAssignmentRepository
    {
        public List<ActionAssignment> Rows { get; } = new();
        public int LoadCount;
        public TaskCompletionSource? LoadGate;

        public async Task<IReadOnlyList<ActionAssignment>> LoadAllAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref LoadCount);
            // Capture the view BEFORE blocking, so a gated load models a read
            // that BEGAN before a concurrent write (the F2 race shape).
            var view = Rows.ToList();
            if (LoadGate is not null) await LoadGate.Task;
            return view;
        }

        public Task<IReadOnlyList<ActionAssignment>> ListPlatformAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActionAssignment>>(
                Rows.Where(r => r.TenantId is null && r.UserId is null).ToList());

        public Task<IReadOnlyList<ActionAssignment>> ListForPrincipalAsync(
            Guid? tenantId, Guid? userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ActionAssignment>>(
                Rows.Where(r => r.TenantId == tenantId && r.UserId == userId).ToList());

        public Task<(ActionAssignment Entity, bool WasCreated)> UpsertAsync(
            Guid? tenantId, Guid? userId, string targetKind, string targetKey,
            int? minAutonomy, bool? enforce, bool? enabled, string[]? allowedRoles,
            string? note, Guid? actingUserId, CancellationToken ct = default)
        {
            var row = Rows.FirstOrDefault(r =>
                r.TenantId == tenantId && r.UserId == userId
                && r.TargetKind == targetKind && r.TargetKey == targetKey);
            var created = row is null;
            if (row is null)
            {
                row = new ActionAssignment
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    UserId = userId,
                    TargetKind = targetKind,
                    TargetKey = targetKey,
                };
                Rows.Add(row);
            }
            if (minAutonomy is not null) row.MinAutonomy = minAutonomy;
            if (enforce is not null) row.Enforce = enforce;
            if (enabled is not null) row.Enabled = enabled;
            if (allowedRoles is not null) row.AllowedRoles = allowedRoles;
            row.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult((row, created));
        }

        public Task<bool> DeleteAsync(
            Guid? tenantId, Guid? userId, string targetKind, string targetKey,
            CancellationToken ct = default)
        {
            var row = Rows.FirstOrDefault(r =>
                r.TenantId == tenantId && r.UserId == userId
                && r.TargetKind == targetKind && r.TargetKey == targetKey);
            if (row is null) return Task.FromResult(false);
            Rows.Remove(row);
            return Task.FromResult(true);
        }

        public Task<int> DeleteAllForPrincipalAsync(
            Guid? tenantId, Guid? userId, CancellationToken ct = default)
            => Task.FromResult(Rows.RemoveAll(r => r.TenantId == tenantId && r.UserId == userId));
    }

    private sealed class FixedMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private static ActionAssignment Row(
        Guid? tenantId, Guid? userId, string kind, string key, int? min,
        bool? enabled = null, DateTime? updatedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = userId,
        TargetKind = kind,
        TargetKey = key,
        MinAutonomy = min,
        Enabled = enabled,
        UpdatedAt = updatedAt ?? DateTime.UtcNow,
    };

    private static GovernancePolicySnapshotStore Store(
        FakeRepository? repo, TammaMode mode = TammaMode.SaaS, TimeProvider? time = null)
        => new(repo, new FixedMode(mode),
            NullLogger<GovernancePolicySnapshotStore>.Instance, time);

    [Test]
    public async Task OneRead_ServesManyGateCalls_WithinTheTtl()
    {
        var repo = new FakeRepository();
        var tid = Guid.NewGuid();
        repo.Rows.Add(Row(tid, null, "action", "tool:file_write", 95));
        var store = Store(repo);

        await store.RefreshAsync();
        var loadsAfterPrime = repo.LoadCount;

        for (var i = 0; i < 40; i++)
        {
            _ = store.GetSnapshot(GovernancePrincipal.ForTenant(tid));
            _ = store.GetSnapshotForAmbient(tid);
        }

        repo.LoadCount.Should().Be(loadsAfterPrime,
            "a 40-call tool loop must ride ONE repository read (AC12's intent)");
        store.GetSnapshot(GovernancePrincipal.ForTenant(tid))
            .PrincipalActionRows["tool:file_write"].MinAutonomy.Should().Be(95);
    }

    [Test]
    public async Task PlatformRows_AreNeverProjectedAsPrincipalRows()
    {
        var repo = new FakeRepository();
        var tid = Guid.NewGuid();
        repo.Rows.Add(Row(null, null, "action", "tool:shell_execute", AutonomyDial_AlwaysHuman()));
        repo.Rows.Add(Row(tid, null, "action", "tool:file_write", 90));
        var store = Store(repo);
        await store.RefreshAsync();

        var snapshot = store.GetSnapshot(GovernancePrincipal.ForTenant(tid));

        snapshot.PlatformActionRows.Should().ContainKey("tool:shell_execute");
        snapshot.PrincipalActionRows.Should().NotContainKey("tool:shell_execute",
            "the ceiling is applied by the evaluator via max(), never by union "
            + "(the behavioural half of 43-5 D2)");
        snapshot.PrincipalActionRows.Should().ContainKey("tool:file_write");
    }

    [Test]
    public async Task PrincipalProjection_IsIsolatedPerTenant()
    {
        var repo = new FakeRepository();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        repo.Rows.Add(Row(a, null, "action", "tool:file_write", 90));
        var store = Store(repo);
        await store.RefreshAsync();

        store.GetSnapshot(GovernancePrincipal.ForTenant(a))
            .PrincipalActionRows.Should().ContainKey("tool:file_write");
        store.GetSnapshot(GovernancePrincipal.ForTenant(b))
            .PrincipalActionRows.Should().BeEmpty("another tenant's rows must never leak");
    }

    [Test]
    public async Task SingleUserAmbient_UsesCollapsedUserRows()
    {
        var repo = new FakeRepository();
        var uid = Guid.NewGuid();
        repo.Rows.Add(Row(null, uid, "group", "code-write", 95));
        var store = Store(repo, TammaMode.SingleUser);
        await store.RefreshAsync();

        var ambient = store.GetSnapshotForAmbient(tenantId: null);
        ambient.PrincipalGroupRows.Should().ContainKey("code-write",
            "the sync tool-loop gate has no resolved user id; the sole user's rows apply");

        // The exact per-user projection agrees.
        store.GetSnapshot(GovernancePrincipal.ForUser(uid))
            .PrincipalGroupRows.Should().ContainKey("code-write");
    }

    [Test]
    public async Task StaleLoad_CanNeverClobberANewerInstall()
    {
        // The ProviderSettingsStore review-F2 property, reproved here: a slow
        // load that BEGAN before a write completes AFTER the write's refresh —
        // its install must be discarded.
        var repo = new FakeRepository();
        var tid = Guid.NewGuid();
        var store = Store(repo);

        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        repo.LoadGate = gate;
        var slow = store.RefreshAsync(); // ticket 1, blocked AFTER reading the empty table's count

        repo.LoadGate = null;
        repo.Rows.Add(Row(tid, null, "action", "tool:file_write", 99));
        await store.RefreshAsync(); // ticket 2, installs the post-write snapshot

        gate.SetResult(); // the stale ticket-1 load now completes with the EMPTY row list
        await slow;

        store.GetSnapshot(GovernancePrincipal.ForTenant(tid))
            .PrincipalActionRows.Should().ContainKey("tool:file_write",
                "the stale pre-write load must not swap the empty snapshot back in");
    }

    [Test]
    public async Task NoRepository_ServesEmptySnapshots_ShippedDefaultsApply()
    {
        var store = Store(repo: null);
        await store.RefreshAsync(); // no-op, never throws

        var snapshot = store.GetSnapshot(GovernancePrincipal.ForTenant(Guid.NewGuid()));
        snapshot.PlatformActionRows.Should().BeEmpty();
        snapshot.PrincipalActionRows.Should().BeEmpty();
    }

    [Test]
    public async Task PrimingService_LoadsTheSnapshotBeforeTraffic_AndIsFailSoft()
    {
        var repo = new FakeRepository();
        var tid = Guid.NewGuid();
        repo.Rows.Add(Row(tid, null, "action", "tool:file_write", 90));
        var store = Store(repo);

        var priming = new GovernancePolicySnapshotPrimingService(
            store, NullLogger<GovernancePolicySnapshotPrimingService>.Instance);
        await priming.StartAsync(CancellationToken.None);

        store.GetSnapshot(GovernancePrincipal.ForTenant(tid))
            .PrincipalActionRows.Should().ContainKey("tool:file_write",
                "the snapshot must be primed before the host serves traffic");

        // Fail-soft: a store whose refresh throws must not break startup.
        var throwing = new ThrowingProvider();
        var failSoft = new GovernancePolicySnapshotPrimingService(
            throwing, NullLogger<GovernancePolicySnapshotPrimingService>.Instance);
        await failSoft.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    private sealed class ThrowingProvider : IGovernancePolicySnapshotProvider
    {
        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal)
            => GovernancePolicySnapshot.Empty;
        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId)
            => GovernancePolicySnapshot.Empty;
        public Task RefreshAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("db down");
    }

    private static int AutonomyDial_AlwaysHuman()
        => Tamma.Core.Documents.Policy.AutonomyDial.AlwaysHuman;
}
