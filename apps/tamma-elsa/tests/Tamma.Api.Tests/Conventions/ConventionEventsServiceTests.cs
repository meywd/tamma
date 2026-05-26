using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Tests for <see cref="ConventionEventsService"/>: emits CONVENTION.* DCB events
/// into the event repository with correct types, tags, and payloads.
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

    // ======================================================================
    // Tenant override events
    // ======================================================================

    [Test]
    public async Task EmitTenantOverrideUpsertedAsync_WasCreated_EmitsCreatedEvent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.EmitTenantOverrideUpsertedAsync(tenantId, Role, Action, userId, wasCreated: true, newVersion: 1, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.CreatedType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.CreatedType);
        evt.TenantId.Should().Be(tenantId);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("role").GetString().Should().Be(RoleWire);
        tags.RootElement.GetProperty("action").GetString().Should().Be(ActionWire);
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString());
        tags.RootElement.GetProperty("userId").GetString().Should().Be(userId.ToString());

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("wasCreated").GetBoolean().Should().BeTrue();
        data.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task EmitTenantOverrideUpsertedAsync_WasUpdated_EmitsUpdatedEvent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.EmitTenantOverrideUpsertedAsync(tenantId, Role, Action, userId, wasCreated: false, newVersion: 3, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.UpdatedType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.UpdatedType);

        using var data = JsonDocument.Parse(events[0].Data);
        data.RootElement.GetProperty("wasCreated").GetBoolean().Should().BeFalse();
        data.RootElement.GetProperty("version").GetInt32().Should().Be(3);
    }

    [Test]
    public async Task EmitTenantOverrideDeletedAsync_WasDeleted_EmitsDeletedEvent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.EmitTenantOverrideDeletedAsync(tenantId, Role, Action, userId, wasDeleted: true, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.DeletedType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.DeletedType);
        events[0].TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task EmitTenantOverrideDeletedAsync_WasNotDeleted_EmitsNothing()
    {
        var tenantId = Guid.NewGuid();

        // No-op delete: wasDeleted = false — must emit NOTHING.
        await _service.EmitTenantOverrideDeletedAsync(tenantId, Role, Action, Guid.Empty, wasDeleted: false, ct: default);

        var events = await _eventRepo.QueryAsync(tenantId, ConventionEventsService.DeletedType, null, 10);
        events.Should().BeEmpty("a no-op delete must not produce an audit event");
    }

    // ======================================================================
    // System-default events
    // ======================================================================

    [Test]
    public async Task EmitSystemDefaultUpsertedAsync_WasCreated_EmitsSystemDefaultCreatedEvent()
    {
        var adminId = Guid.NewGuid();

        await _service.EmitSystemDefaultUpsertedAsync(Role, Action, adminId, wasCreated: true, newVersion: 1, ct: default);

        // System-default events have null TenantId — query with null
        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.SystemDefaultCreatedType, null, 10);
        events.Should().HaveCount(1);

        var evt = events[0];
        evt.Type.Should().Be(ConventionEventsService.SystemDefaultCreatedType);
        evt.TenantId.Should().BeNull("system-default events are platform-wide, not tenant-scoped");

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("role").GetString().Should().Be(RoleWire);
        tags.RootElement.GetProperty("action").GetString().Should().Be(ActionWire);
        tags.RootElement.GetProperty("userId").GetString().Should().Be(adminId.ToString());
        tags.RootElement.TryGetProperty("tenantId", out _).Should().BeFalse("tenantId must be absent for platform-wide events");
    }

    [Test]
    public async Task EmitSystemDefaultUpsertedAsync_WasUpdated_EmitsSystemDefaultUpdatedEvent()
    {
        var adminId = Guid.NewGuid();

        await _service.EmitSystemDefaultUpsertedAsync(Role, Action, adminId, wasCreated: false, newVersion: 5, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.SystemDefaultUpdatedType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.SystemDefaultUpdatedType);
    }

    [Test]
    public async Task EmitSystemDefaultDeletedAsync_WasDeleted_EmitsEvent()
    {
        var adminId = Guid.NewGuid();

        await _service.EmitSystemDefaultDeletedAsync(Role, Action, adminId, wasDeleted: true, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.SystemDefaultDeletedType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.SystemDefaultDeletedType);
        events[0].TenantId.Should().BeNull();
    }

    [Test]
    public async Task EmitSystemDefaultDeletedAsync_WasNotDeleted_EmitsNothing()
    {
        var adminId = Guid.NewGuid();

        // No-op delete: wasDeleted = false — must emit NOTHING.
        await _service.EmitSystemDefaultDeletedAsync(Role, Action, adminId, wasDeleted: false, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.SystemDefaultDeletedType, null, 10);
        events.Should().BeEmpty("a no-op delete must not produce an audit event");
    }

    [Test]
    public async Task EmitSystemDefaultResetAsync_AlwaysEmits()
    {
        var adminId = Guid.NewGuid();

        await _service.EmitSystemDefaultResetAsync(Role, Action, adminId, ct: default);

        var events = await _eventRepo.QueryAsync(null, ConventionEventsService.SystemDefaultResetType, null, 10);
        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ConventionEventsService.SystemDefaultResetType);
        events[0].TenantId.Should().BeNull();
    }

    // ======================================================================
    // Best-effort swallowing
    // ======================================================================

    [Test]
    public async Task EmitTenantOverrideUpsertedAsync_SwallowsRepositoryExceptions()
    {
        var throwingRepo = new ThrowingEventRepository();
        var service = new ConventionEventsService(throwingRepo);

        var act = () => service.EmitTenantOverrideUpsertedAsync(
            Guid.NewGuid(), Role, Action, Guid.NewGuid(), wasCreated: true, newVersion: 1, ct: default);
        await act.Should().NotThrowAsync("event-store failures must never block the caller");
    }

    private sealed class ThrowingEventRepository : IEventRepository
    {
        public Task<Tamma.Data.Entities.DomainEvent> AppendAsync(Tamma.Data.Entities.DomainEvent evt)
            => throw new InvalidOperationException("simulated event-store failure");

        public Task<Tamma.Data.Entities.DomainEvent?> GetByIdAsync(Guid id)
            => throw new NotImplementedException();

        public Task<List<Tamma.Data.Entities.DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => throw new NotImplementedException();

        public Task<Tamma.Data.Entities.DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => throw new NotImplementedException();

        public Task ClearAsync(Guid tenantId) => throw new NotImplementedException();

        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => throw new NotImplementedException();

        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => throw new NotImplementedException();
    }
}
