using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Unified-tenancy Phase 4 Task 4 — tests for the <c>tenant.move</c>
/// platform-queue handler. Mirrors the
/// <c>ProvisionTenantV2TaskHandlerTests</c> shape (the Cranl pattern this
/// handler copies): payload-shape failures are terminal (straight to
/// dead-letter), execution failures bubble (worker retry budget), and the
/// happy path drives the underlying service with the payload's arguments.
///
/// <para>Uses EF InMemory for the control plane (the FailureReason /
/// UpdatedAt bookkeeping is plain shadow-column writes) and recording /
/// throwing fakes for <see cref="ITenantMoveService"/>.</para>
/// </summary>
[TestFixture]
public sealed class MoveTenantTaskHandlerTests
{
    private ControlPlaneDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private sealed class RecordingMoveService : ITenantMoveService
    {
        public List<(Guid TenantId, Guid TargetDatabaseId)> Calls { get; } = new();
        public Exception? ThrowOnMove { get; set; }

        public Task MoveAsync(
            Guid tenantId, Guid targetDatabaseId, CancellationToken ct = default)
        {
            Calls.Add((tenantId, targetDatabaseId));
            if (ThrowOnMove is not null) throw ThrowOnMove;
            return Task.CompletedTask;
        }
    }

    private MoveTenantTaskHandler BuildHandler(RecordingMoveService move) =>
        new(move, _db, TimeProvider.System,
            NullLogger<MoveTenantTaskHandler>.Instance);

    private async Task<Tenant> SeedTenantAsync(string? failureReason = null)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        _db.Tenants.Add(tenant);
        _db.Entry(tenant).Property("Status").CurrentValue = "active";
        _db.Entry(tenant).Property("FailureReason").CurrentValue = failureReason;
        await _db.SaveChangesAsync();
        return tenant;
    }

    private static PlatformQueuedTask BuildTask(string payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = MoveTenantTaskPayload.TaskType,
        Payload = payload,
    };

    private static PlatformQueuedTask BuildTask(Guid tenantId, Guid targetDatabaseId) =>
        BuildTask(JsonSerializer.Serialize(new MoveTenantTaskPayload
        {
            TenantId = tenantId,
            TargetDatabaseId = targetDatabaseId,
        }));

    private async Task<string?> FailureReasonOfAsync(Guid tenantId)
    {
        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        return (string?)_db.Entry(tenant).Property("FailureReason").CurrentValue;
    }

    // ── routing + payload shape ──

    [Test]
    public void TaskType_MatchesPlatformQueueIdentifier()
    {
        var handler = BuildHandler(new RecordingMoveService());
        handler.TaskType.Should().Be("tenant.move");
        handler.TaskType.Should().Be(MoveTenantTaskPayload.TaskType);
    }

    [Test]
    public async Task HandleAsync_NullTask_Throws()
    {
        var handler = BuildHandler(new RecordingMoveService());
        Func<Task> act = async () => await handler.HandleAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task HandleAsync_MalformedJson_ThrowsTerminalException()
    {
        var move = new RecordingMoveService();
        var handler = BuildHandler(move);

        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask("not-valid-json {"), CancellationToken.None);

        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
        move.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task HandleAsync_EmptyPayload_ThrowsTerminalException()
    {
        var handler = BuildHandler(new RecordingMoveService());
        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask(""), CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_EmptyTenantId_ThrowsTerminalException()
    {
        var handler = BuildHandler(new RecordingMoveService());
        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask(Guid.Empty, Guid.NewGuid()), CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task HandleAsync_EmptyTargetDatabaseId_ThrowsTerminalException()
    {
        var handler = BuildHandler(new RecordingMoveService());
        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask(Guid.NewGuid(), Guid.Empty), CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    // ── success path ──

    [Test]
    public async Task HandleAsync_HappyPath_InvokesMoveWithPayloadArguments()
    {
        var tenant = await SeedTenantAsync();
        var targetId = Guid.NewGuid();
        var move = new RecordingMoveService();
        var handler = BuildHandler(move);

        await handler.HandleAsync(BuildTask(tenant.Id, targetId), CancellationToken.None);

        move.Calls.Should().ContainSingle()
            .Which.Should().Be((tenant.Id, targetId));
    }

    [Test]
    public async Task HandleAsync_Success_ClearsStaleFailureReason()
    {
        var tenant = await SeedTenantAsync(failureReason: "old move error");
        var move = new RecordingMoveService();
        var handler = BuildHandler(move);

        await handler.HandleAsync(
            BuildTask(tenant.Id, Guid.NewGuid()), CancellationToken.None);

        (await FailureReasonOfAsync(tenant.Id)).Should().BeNull(
            "a retried task that eventually succeeds must not leave the "
            + "admin UX reporting the previous attempt's error");
    }

    // ── failure path ──

    [Test]
    public async Task HandleAsync_MoveThrows_WritesFailureReason_BumpsUpdatedAt_AndRethrows()
    {
        var tenant = await SeedTenantAsync();
        var before = tenant.UpdatedAt;
        var move = new RecordingMoveService
        {
            ThrowOnMove = new InvalidOperationException("restore verify mismatch"),
        };
        var handler = BuildHandler(move);

        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask(tenant.Id, Guid.NewGuid()), CancellationToken.None);

        // NOT terminal — the worker's FailAsync re-enqueues with the retry
        // budget (mirrors how the v2 provisioning handler bubbles
        // unexpected exceptions).
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("restore verify mismatch");

        var reason = await FailureReasonOfAsync(tenant.Id);
        reason.Should().Be("InvalidOperationException: restore verify mismatch");
        var refreshed = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenant.Id);
        refreshed.UpdatedAt.Should().BeAfter(before);
    }

    [Test]
    public async Task HandleAsync_AdvisoryLockRejection_IsRetryable_NotTerminal()
    {
        // The move engine rejects a concurrent move with a plain
        // InvalidOperationException ("already in progress"). The handler
        // deliberately treats it as retryable: the competing move finishes
        // (or fails) and a later re-fire of the queued task proceeds.
        var tenant = await SeedTenantAsync();
        var move = new RecordingMoveService
        {
            ThrowOnMove = new InvalidOperationException(
                $"A move for tenant '{tenant.Id}' is already in progress (the "
                + "per-tenant control-plane advisory lock is held by another "
                + "session) — wait for it to finish or fail before retrying."),
        };
        var handler = BuildHandler(move);

        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask(tenant.Id, Guid.NewGuid()), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().NotBeOfType<PlatformTaskTerminalException>(
            "the lock rejection must burn a retry, not dead-letter the task");
        (await FailureReasonOfAsync(tenant.Id)).Should().Contain("already in progress");
    }

    [Test]
    public async Task HandleAsync_MoveThrows_TenantRowMissing_StillRethrowsOriginal()
    {
        // FailureReason stamping is best-effort bookkeeping — a missing
        // tenant row (e.g. deleted mid-flight) must not mask the original
        // move exception.
        var move = new RecordingMoveService
        {
            ThrowOnMove = new InvalidOperationException("boom"),
        };
        var handler = BuildHandler(move);

        Func<Task> act = async () => await handler.HandleAsync(
            BuildTask(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("boom");
    }
}
