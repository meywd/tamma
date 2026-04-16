using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Tests for <see cref="PromptEventsService"/>: emits PROMPT.* DCB events into
/// the event repository with correct tags and payloads.
/// </summary>
[TestFixture]
public class PromptEventsServiceTests
{
    private TammaDbContext _db = null!;
    private EventRepository _eventRepo = null!;
    private PromptEventsService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<TammaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new TestDbContext(options);
        _eventRepo = new EventRepository(_db);
        _service = new PromptEventsService(_eventRepo);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task EmitUpdatedAsync_AppendsPromptUpdatedSuccessEvent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.EmitUpdatedAsync(tenantId, userId, "developer", "plan", new Dictionary<string, object?>
        {
            ["template"] = "new template",
        });

        var events = await _eventRepo.QueryAsync(tenantId, "PROMPT.UPDATED.SUCCESS", null, 10);

        events.Should().HaveCount(1);
        var evt = events[0];
        evt.Type.Should().Be("PROMPT.UPDATED.SUCCESS");
        evt.TenantId.Should().Be(tenantId);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("role").GetString().Should().Be("developer");
        tags.RootElement.GetProperty("action").GetString().Should().Be("plan");
        tags.RootElement.GetProperty("userId").GetString().Should().Be(userId.ToString());
    }

    [Test]
    public async Task EmitDeletedAsync_AppendsPromptDeletedSuccessEvent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.EmitDeletedAsync(tenantId, userId, "tester", "write-tests");

        var events = await _eventRepo.QueryAsync(tenantId, "PROMPT.DELETED.SUCCESS", null, 10);

        events.Should().HaveCount(1);
        var evt = events[0];
        evt.Type.Should().Be("PROMPT.DELETED.SUCCESS");
        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("role").GetString().Should().Be("tester");
        tags.RootElement.GetProperty("action").GetString().Should().Be("write-tests");
    }

    [Test]
    public async Task EmitRenderedAsync_AppendsPromptRenderedSuccessEvent_WithVariableCount()
    {
        var tenantId = Guid.NewGuid();

        await _service.EmitRenderedAsync(tenantId, null, "architect", "code-review",
            variableCount: 5,
            unresolvedCount: 2);

        var events = await _eventRepo.QueryAsync(tenantId, "PROMPT.RENDERED.SUCCESS", null, 10);

        events.Should().HaveCount(1);
        using var data = JsonDocument.Parse(events[0].Data);
        data.RootElement.GetProperty("variableCount").GetInt32().Should().Be(5);
        data.RootElement.GetProperty("unresolvedCount").GetInt32().Should().Be(2);
    }

    [Test]
    public async Task EmitUpdatedAsync_SwallowsRepositoryExceptions()
    {
        var throwingRepo = new ThrowingEventRepository();
        var service = new PromptEventsService(throwingRepo);

        // Should not bubble up
        var act = () => service.EmitUpdatedAsync(
            Guid.NewGuid(), null, "developer", "plan",
            new Dictionary<string, object?>());
        await act.Should().NotThrowAsync();
    }

    private sealed class ThrowingEventRepository : IEventRepository
    {
        public Task<Tamma.Data.Entities.DomainEvent> AppendAsync(Tamma.Data.Entities.DomainEvent evt)
            => throw new InvalidOperationException("simulated");

        public Task<Tamma.Data.Entities.DomainEvent?> GetByIdAsync(Guid id) => throw new NotImplementedException();

        public Task<List<Tamma.Data.Entities.DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => throw new NotImplementedException();

        public Task<Tamma.Data.Entities.DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => throw new NotImplementedException();

        public Task ClearAsync(Guid tenantId) => throw new NotImplementedException();
    }
}
