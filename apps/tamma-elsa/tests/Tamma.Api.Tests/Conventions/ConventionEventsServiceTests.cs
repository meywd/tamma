using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Tests for <see cref="ConventionEventsService"/> (Story 27-14 refactor):
/// emits unified CONVENTION.* DCB events with <c>source</c> tag, correct
/// payloads, and <c>changedFields</c> diff on UPDATED events.
///
/// Coverage intent:
/// <list type="bullet">
///   <item>CREATED and UPDATED for tenant override (source=tenant, tenantId tag present).</item>
///   <item>CREATED and UPDATED for system default (source=system, tenantId tag absent).</item>
///   <item>DELETED for tenant and system (wasDeleted=false emits nothing).</item>
///   <item>RESET emits RESET event with source=system, previousVersion, newVersion.</item>
///   <item>UPDATED carries changedFields diff (body / enabled).</item>
///   <item>Best-effort: event-store failures never throw to the caller.</item>
/// </list>
///
/// Mirrors <c>PromptEventsServiceTests</c>.
/// </summary>
[TestFixture]
public class ConventionEventsServiceTests
{
    private const AgentRole Role = AgentRole.Developer;
    private const AgentAction Action = AgentAction.ImplementFeature;
    private static readonly string RoleWire = Role.ToWire();
    private static readonly string ActionWire = Action.ToWire();

    private InMemoryDbFixture _fx = null!;
    private ControlPlaneDbContext _db = null!;
    private EventRepository _eventRepo = null!;
    private ConventionEventsService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new InMemoryDbFixture();
        _db = _fx.Cp;
        _eventRepo = new EventRepository(
            _fx.Factory,
            new TenantContext(),
            new PlatformEventRepository(_db));
        _service = new ConventionEventsService(_eventRepo);
    }

    [TearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    // Convenience: a minimal Convention snapshot.
    private static Convention Row(string body, bool enabled, int version) => new()
    {
        Id = Guid.NewGuid(),
        Role = RoleWire,
        Action = ActionWire,
        Body = body,
        Enabled = enabled,
        Version = version,
    };

    // ======================================================================
    // Tenant override — CREATED
    // ======================================================================

    [Test]
    public async Task EmitUpserted_Tenant_WasCreated_EmitsCreatedEvent_WithSourceTenant()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var current = Row("BODY", true, 1);

        await _service.EmitUpsertedAsync(tenantId, Role, Action, userId, wasCreated: true,
            previous: null, current: current, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.CreatedType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.CreatedType);
        evt.TenantId.Should().Be(tenantId);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("role").GetString().Should().Be(RoleWire);
        tags.RootElement.GetProperty("action").GetString().Should().Be(ActionWire);
        tags.RootElement.GetProperty("source").GetString().Should().Be("tenant");
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString());
        tags.RootElement.GetProperty("userId").GetString().Should().Be(userId.ToString());

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        data.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    // ======================================================================
    // Tenant override — UPDATED with changedFields
    // ======================================================================

    [Test]
    public async Task EmitUpserted_Tenant_WasUpdated_BodyChanged_EmitsUpdatedWithChangedFields()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var previous = Row("OLD-BODY", true, 2);
        var current = Row("NEW-BODY", true, 3);

        await _service.EmitUpsertedAsync(tenantId, Role, Action, userId, wasCreated: false,
            previous: previous, current: current, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.UpdatedType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.UpdatedType);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("source").GetString().Should().Be("tenant");
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString());
        tags.RootElement.GetProperty("userId").GetString().Should().Be(userId.ToString());

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("previousVersion").GetInt32().Should().Be(2);
        data.RootElement.GetProperty("newVersion").GetInt32().Should().Be(3);

        var changedFields = data.RootElement.GetProperty("changedFields")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        changedFields.Should().Contain("body");
        changedFields.Should().NotContain("enabled");
    }

    [Test]
    public async Task EmitUpserted_Tenant_WasUpdated_EnabledChanged_EmitsUpdatedWithChangedFields()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var previous = Row("SAME-BODY", true, 1);
        var current = Row("SAME-BODY", false, 2);

        await _service.EmitUpsertedAsync(tenantId, Role, Action, userId, wasCreated: false,
            previous: previous, current: current, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.UpdatedType, null, 10);
        events.Should().HaveCount(1);

        using var data = JsonDocument.Parse(events[0].Data);
        var changedFields = data.RootElement.GetProperty("changedFields")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        changedFields.Should().Contain("enabled");
        changedFields.Should().NotContain("body");
    }

    [Test]
    public async Task EmitUpserted_Tenant_WasUpdated_BothChanged_EmitsBothInChangedFields()
    {
        var tenantId = Guid.NewGuid();
        var previous = Row("OLD", true, 1);
        var current = Row("NEW", false, 2);

        await _service.EmitUpsertedAsync(tenantId, Role, Action, Guid.NewGuid(), wasCreated: false,
            previous: previous, current: current, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.UpdatedType, null, 10);
        events.Should().HaveCount(1);

        using var data = JsonDocument.Parse(events[0].Data);
        var changedFields = data.RootElement.GetProperty("changedFields")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        // Positional assertion — locks the deterministic order (body before enabled)
        // so a future reorder of the if-blocks would surface as a test failure.
        changedFields.Should().Equal(new[] { "body", "enabled" });
    }

    // ======================================================================
    // Tenant override — DELETED
    // ======================================================================

    [Test]
    public async Task EmitDeleted_Tenant_WasDeleted_EmitsDeletedEvent_WithSourceTenant()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.EmitDeletedAsync(tenantId, Role, Action, userId,
            wasDeleted: true, deletedVersion: 4, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.DeletedType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.DeletedType);
        evt.TenantId.Should().Be(tenantId);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("source").GetString().Should().Be("tenant");
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString());

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("deletedVersion").GetInt32().Should().Be(4);
    }

    [Test]
    public async Task EmitDeleted_Tenant_WasNotDeleted_EmitsNothing()
    {
        var tenantId = Guid.NewGuid();

        await _service.EmitDeletedAsync(tenantId, Role, Action, Guid.Empty,
            wasDeleted: false, deletedVersion: null, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.DeletedType, null, 10);
        events.Should().BeEmpty("a no-op delete must not produce an audit event");
    }

    // ======================================================================
    // System default — CREATED (source=system, no tenantId tag)
    // ======================================================================

    [Test]
    public async Task EmitUpserted_System_WasCreated_EmitsCreatedEvent_WithSourceSystem_NoTenantId()
    {
        var adminId = Guid.NewGuid();
        var current = Row("DEFAULT-BODY", true, 1);

        await _service.EmitUpsertedAsync(null, Role, Action, adminId, wasCreated: true,
            previous: null, current: current, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.CreatedType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.CreatedType);
        evt.TenantId.Should().BeNull("system-default events are platform-wide, not tenant-scoped");

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("source").GetString().Should().Be("system");
        tags.RootElement.GetProperty("userId").GetString().Should().Be(adminId.ToString());
        tags.RootElement.TryGetProperty("tenantId", out _).Should().BeFalse(
            "tenantId must be absent for platform-wide events");

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task EmitUpserted_System_WasUpdated_EmitsUpdatedEvent_WithChangedFields()
    {
        var adminId = Guid.NewGuid();
        var previous = Row("OLD-DEFAULT", true, 3);
        var current = Row("NEW-DEFAULT", true, 4);

        await _service.EmitUpsertedAsync(null, Role, Action, adminId, wasCreated: false,
            previous: previous, current: current, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.UpdatedType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.UpdatedType);
        events[0].TenantId.Should().BeNull();

        using var tags = JsonDocument.Parse(events[0].Tags);
        tags.RootElement.GetProperty("source").GetString().Should().Be("system");

        using var data = JsonDocument.Parse(events[0].Data);
        data.RootElement.GetProperty("previousVersion").GetInt32().Should().Be(3);
        data.RootElement.GetProperty("newVersion").GetInt32().Should().Be(4);

        var changedFields = data.RootElement.GetProperty("changedFields")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        changedFields.Should().Contain("body");
    }

    // ======================================================================
    // System default — DELETED
    // ======================================================================

    [Test]
    public async Task EmitDeleted_System_WasDeleted_EmitsDeletedEvent_WithSourceSystem()
    {
        var adminId = Guid.NewGuid();

        await _service.EmitDeletedAsync(null, Role, Action, adminId,
            wasDeleted: true, deletedVersion: 2, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.DeletedType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.DeletedType);
        events[0].TenantId.Should().BeNull();

        using var tags = JsonDocument.Parse(events[0].Tags);
        tags.RootElement.GetProperty("source").GetString().Should().Be("system");
    }

    [Test]
    public async Task EmitDeleted_System_WasNotDeleted_EmitsNothing()
    {
        await _service.EmitDeletedAsync(null, Role, Action, Guid.NewGuid(),
            wasDeleted: false, deletedVersion: null, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.DeletedType, null, 10);
        events.Should().BeEmpty("a no-op delete must not produce an audit event");
    }

    // ======================================================================
    // RESET
    // ======================================================================

    [Test]
    public async Task EmitReset_AlwaysEmits_WithPreviousAndNewVersion_SourceSystem()
    {
        var adminId = Guid.NewGuid();

        await _service.EmitResetAsync(Role, Action, adminId,
            previousVersion: 5, newVersion: 6, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.ResetType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.ResetType);
        evt.TenantId.Should().BeNull();

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("source").GetString().Should().Be("system");
        tags.RootElement.GetProperty("userId").GetString().Should().Be(adminId.ToString());
        tags.RootElement.TryGetProperty("tenantId", out _).Should().BeFalse();

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("previousVersion").GetInt32().Should().Be(5);
        data.RootElement.GetProperty("newVersion").GetInt32().Should().Be(6);
        data.RootElement.GetProperty("resetFrom").GetString().Should().Be("custom");
        data.RootElement.GetProperty("resetTo").GetString().Should().Be("hardcoded");
    }

    // ======================================================================
    // Best-effort swallowing
    // ======================================================================

    [Test]
    public async Task EmitUpserted_SwallowsRepositoryExceptions()
    {
        var throwingRepo = new ThrowingEventRepository();
        var service = new ConventionEventsService(throwingRepo);
        var current = Row("BODY", true, 1);

        var act = () => service.EmitUpsertedAsync(
            Guid.NewGuid(), Role, Action, Guid.NewGuid(), wasCreated: true,
            previous: null, current: current, ct: default);
        await act.Should().NotThrowAsync("event-store failures must never block the caller");
    }

    // ======================================================================
    // Dropped event type names — confirmed absent via zero-occurrence grep
    // (see Story 27-14 requirements; compile-time check: the old const names
    //  are gone, so any reference would fail to compile).
    // ======================================================================

    // (No test needed: the deleted constants SystemDefaultCreatedType etc.
    //  simply don't exist anymore — any stale reference is a compile error.)

    private sealed class ThrowingEventRepository : IEventRepository
    {
        public Task<DomainEvent> AppendAsync(DomainEvent evt)
            => throw new InvalidOperationException("simulated event-store failure");

        public Task<DomainEvent?> GetByIdAsync(Guid id)
            => throw new NotImplementedException();

        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => throw new NotImplementedException();

        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => throw new NotImplementedException();

        public Task ClearAsync(Guid tenantId) => throw new NotImplementedException();

        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => throw new NotImplementedException();

        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => throw new NotImplementedException();
    }
}
