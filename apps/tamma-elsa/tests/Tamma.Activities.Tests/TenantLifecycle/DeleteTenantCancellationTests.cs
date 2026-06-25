using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Review-finding coverage for the DESTRUCTIVE delete workflow:
///
/// <list type="bullet">
///   <item>[CRITICAL] Cancellation race — a cancel that lands AFTER the trigger
///     dispatches must NOT result in a dropped schema. The mark step + the
///     cancellation guard each re-read Status; if it is no longer 'deleting'
///     they ABORT the run (set the abort flag) WITHOUT resurrecting the row,
///     and the terminal emits ABORTED instead of dropping.</item>
///   <item>[CRITICAL] Self re-dispatch — the workflow no longer re-emits
///     TENANT.DELETE.REQUESTED (the trigger's own poll target); the mark step
///     emits the distinct TENANT.DELETE.STARTED marker.</item>
///   <item>Abort short-circuit — once aborted, every destructive step skips.</item>
/// </list>
///
/// All exercised through the pure-DI seams (EF InMemory + in-memory
/// <see cref="ICleanupStateStore"/>) so the race is testable without an Elsa
/// runtime.
/// </summary>
[TestFixture]
public class DeleteTenantCancellationTests
{
    private ControlPlaneDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<Guid> SeedTenantAsync(string status)
    {
        var id = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = id,
            Name = "Acme",
            Slug = "acme-" + id.ToString("N")[..6],
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.Entry(tenant).Property("Status").CurrentValue = status;
        await _db.SaveChangesAsync();
        return id;
    }

    // ───────────────────────── MARK STEP — the race ─────────────────────────

    [Test]
    public async Task MarkStep_CancelledTenant_AbortsWithoutResurrecting()
    {
        // The race: trigger dispatched → operator cancelled (deleting→active) →
        // workflow's mark step runs. It must NOT flip active→deleting and MUST
        // abort the run so the schema is never dropped.
        var tenantId = await SeedTenantAsync("active");
        var store = new InMemoryCleanupStateStore();

        var stillDeleting = await MarkTenantDeletingForDeleteActivity.EvaluateAsync(
            _db, store, tenantId, logger: null, CancellationToken.None);

        stillDeleting.Should().BeFalse("a cancelled tenant must not proceed to the destructive span");
        CleanupWorkflowState.IsAborted(store).Should().BeTrue();
        CleanupWorkflowState.GetAbortReason(store).Should().Contain("active");

        // CRITICAL — the row was NOT resurrected to 'deleting'.
        var reloaded = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
        ((string?)_db.Entry(reloaded).Property("Status").CurrentValue)
            .Should().Be("active", "the mark step must never flip a cancelled tenant back to deleting");
    }

    [Test]
    public async Task MarkStep_StillDeleting_ProceedsWithoutAbort()
    {
        var tenantId = await SeedTenantAsync("deleting");
        var store = new InMemoryCleanupStateStore();

        var stillDeleting = await MarkTenantDeletingForDeleteActivity.EvaluateAsync(
            _db, store, tenantId, logger: null, CancellationToken.None);

        stillDeleting.Should().BeTrue();
        CleanupWorkflowState.IsAborted(store).Should().BeFalse();
    }

    [Test]
    public async Task MarkStep_MissingTenant_Throws()
    {
        var store = new InMemoryCleanupStateStore();
        var act = async () => await MarkTenantDeletingForDeleteActivity.EvaluateAsync(
            _db, store, Guid.NewGuid(), logger: null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ──────────────────── GUARD STEP — last line before drop ────────────────

    [Test]
    public async Task GuardStep_CancelledTenant_AbortsBeforeDrop()
    {
        var tenantId = await SeedTenantAsync("active");
        var store = new InMemoryCleanupStateStore();
        var factory = new SingleContextFactory(_db);

        var stillDeleting = await GuardTenantDeletingActivity.EvaluateAsync(
            factory, store, tenantId, logger: null, CancellationToken.None);

        stillDeleting.Should().BeFalse();
        CleanupWorkflowState.IsAborted(store).Should().BeTrue();
        CleanupWorkflowState.GetAbortReason(store).Should().Contain("DROP SCHEMA");
    }

    [Test]
    public async Task GuardStep_StillDeleting_Proceeds()
    {
        var tenantId = await SeedTenantAsync("deleting");
        var store = new InMemoryCleanupStateStore();
        var factory = new SingleContextFactory(_db);

        var stillDeleting = await GuardTenantDeletingActivity.EvaluateAsync(
            factory, store, tenantId, logger: null, CancellationToken.None);

        stillDeleting.Should().BeTrue();
        CleanupWorkflowState.IsAborted(store).Should().BeFalse();
    }

    [Test]
    public async Task GuardStep_ReadFailure_FailsClosed_Aborts()
    {
        // A read failure right before DROP SCHEMA must FAIL CLOSED — abort, not
        // assume "still deleting". Simulated by a factory that throws.
        var store = new InMemoryCleanupStateStore();
        var factory = new ThrowingContextFactory();

        var stillDeleting = await GuardTenantDeletingActivity.EvaluateAsync(
            factory, store, Guid.NewGuid(), logger: null, CancellationToken.None);

        stillDeleting.Should().BeFalse("a CP read failure must abort, never proceed to the drop");
        CleanupWorkflowState.IsAborted(store).Should().BeTrue();
        CleanupWorkflowState.GetAbortReason(store).Should().Contain("fail-closed");
    }

    // ──────────────────── Abort state machine + terminal ────────────────────

    [Test]
    public void Abort_IsIdempotent_KeepsFirstReason()
    {
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.MarkAborted(store, "first");
        CleanupWorkflowState.MarkAborted(store, "second");

        CleanupWorkflowState.IsAborted(store).Should().BeTrue();
        CleanupWorkflowState.GetAbortReason(store).Should().Be("first");
    }

    [Test]
    public void Abort_SkippedStepsTracked_NotInSucceededOrFailed()
    {
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.MarkAborted(store, "cancelled");
        CleanupWorkflowState.RecordSkipped(store, CleanupSteps.DropSchema);
        CleanupWorkflowState.RecordSkipped(store, CleanupSteps.DropRole);

        CleanupWorkflowState.GetSkippedSteps(store).Should()
            .BeEquivalentTo(new[] { CleanupSteps.DropSchema, CleanupSteps.DropRole });
        CleanupWorkflowState.GetSucceededSteps(store).Should().BeEmpty();
        CleanupWorkflowState.GetFailedSteps(store).Should().BeEmpty(
            "a skipped (aborted) step is neither a success nor a failure");
    }

    [Test]
    public void NotAborted_ByDefault()
    {
        var store = new InMemoryCleanupStateStore();
        CleanupWorkflowState.IsAborted(store).Should().BeFalse();
        CleanupWorkflowState.GetAbortReason(store).Should().BeNull();
    }

    // ──────────────────── Self re-dispatch guard (event name) ───────────────

    [Test]
    public void Workflow_DoesNotReEmit_DeleteRequested_UsesDistinctStartedMarker()
    {
        // The self re-dispatch fix: the workflow must NEVER emit the exact event
        // the trigger polls (TENANT.DELETE.REQUESTED). It uses a distinct
        // STARTED marker. Lock the constant so a future edit can't reintroduce
        // the loop without breaking this test.
        TenantLifecycleEvents.DeleteStarted.Should().Be("TENANT.DELETE.STARTED");
        TenantLifecycleEvents.DeleteStarted.Should().NotBe(TenantLifecycleEvents.DeleteRequested);
        TenantLifecycleEvents.DeleteAborted.Should().Be("TENANT.DELETE.ABORTED");
    }

    // ──────────────── Force-delete cooling-off bypass (trigger) ─────────────

    [Test]
    public void Trigger_IsForceDelete_DetectsForceDeleteSource()
    {
        // The force-delete contract: a force-delete request waives the
        // cooling-off window. The trigger detects it from the event payload's
        // source marker (set by ForceDeleteTenant → "admin-force-delete").
        TenantDeleteRequestedTrigger.IsForceDelete(
            """{"source":"admin-force-delete","requestedAt":"x"}""").Should().BeTrue();

        // A normal delete (or any other source) is NOT force — subject to the
        // cooling-off window.
        TenantDeleteRequestedTrigger.IsForceDelete(
            """{"source":"admin-delete"}""").Should().BeFalse();
        TenantDeleteRequestedTrigger.IsForceDelete("{}").Should().BeFalse();
        TenantDeleteRequestedTrigger.IsForceDelete(null).Should().BeFalse();
        // Tolerant of malformed payloads — treated as a normal delete.
        TenantDeleteRequestedTrigger.IsForceDelete("not json").Should().BeFalse();
    }

    // ── Test factories ──

    private sealed class SingleContextFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly ControlPlaneDbContext _ctx;
        public SingleContextFactory(ControlPlaneDbContext ctx) => _ctx = ctx;
        // Return the SAME context — the InMemory store is shared by name, and
        // the guard only reads (AsNoTracking), so re-using the instance is safe
        // and avoids a disposed-context surprise.
        public ControlPlaneDbContext CreateDbContext() => _ctx;
        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(_ctx);
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() =>
            throw new InvalidOperationException("simulated CP read failure");
        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("simulated CP read failure");
    }
}
