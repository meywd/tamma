using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-1 (Task 3) — repository tests for <see cref="AgentRepository"/>
/// against a real Postgres testcontainer (so the versioning transaction, the
/// <c>(AgentId, Version)</c> unique index that guards concurrent publishes, and
/// the ownership CHECK all behave like production). DCB emission is asserted
/// via a capturing <see cref="IEventRepository"/> fake (the real routing is
/// covered by EventRepository's own tests).
///
/// Covers: version increment 1→2→3 + monotonicity; rollback-pointer integrity;
/// prior versions still fetchable; concurrent double-publish race → monotonic,
/// no dup; archive idempotency; per-transition event emission; no event on
/// validation/transaction failure; per-mode principal derivation; ownership
/// guard rejection.
/// </summary>
[TestFixture]
public class AgentRepositoryTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3201-3201-3201-aaaaaaaaaaaa");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3201-3201-3201-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_repo_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE agent_versions, agents CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private (AgentRepository repo, CapturingEventRepository events, ControlPlaneDbContext ctx)
        BuildRepo()
    {
        var ctx = NewContext();
        var events = new CapturingEventRepository();
        return (new AgentRepository(ctx, events), events, ctx);
    }

    private static Agent NewPublicAgent(string name = "tamma-architect", string role = "architect")
        => new() { Name = name, Role = role, Visibility = AgentVisibility.Public };

    private static Agent NewTenantAgent(string name = "atlas", string role = "architect")
        => new()
        {
            Name = name, Role = role, Visibility = AgentVisibility.Private,
            OwnerTenantId = TenantA,
        };

    private static Agent NewUserAgent(string name = "atlas", string role = "architect")
        => new()
        {
            Name = name, Role = role, Visibility = AgentVisibility.Private,
            OwnerUserId = UserA,
        };

    private const string ValidConfig = """{ "provider": "anthropic", "model": "claude-sonnet-4" }""";

    // ── CreateAsync ──

    [Test]
    public async Task CreateAsync_Writes_Agent_And_Version1_SetsPointer_EmitsOneEvent()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var created = await repo.CreateAsync(NewPublicAgent(), ValidConfig, "first", null);

            created.CurrentVersionId.Should().NotBeNull();

            var v1 = await repo.GetVersionAsync(created.Id, 1);
            v1.Should().NotBeNull();
            v1!.Version.Should().Be(1);
            created.CurrentVersionId.Should().Be(v1.Id);

            events.Events.Should().ContainSingle();
            var evt = events.Events.Single();
            evt.Type.Should().Be("AGENT.CREATED.SUCCESS");
            var tags = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Tags)!;
            tags["agentId"].GetString().Should().Be(created.Id.ToString());
            tags["version"].GetInt32().Should().Be(1);
            tags["visibility"].GetString().Should().Be("public");
            tags["role"].GetString().Should().Be("architect");
            tags["mode"].GetString().Should().Be(
                "platform", "a public agent is platform-owned, not a saas tenant agent");
            evt.TenantId.Should().BeNull("public agents emit to the platform feed");
        }
    }

    [Test]
    public async Task CreateAsync_PrivateTenantAgent_EmitsEvent_WithOwnerTenant()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var created = await repo.CreateAsync(NewTenantAgent(), ValidConfig, null, null);

            var evt = events.Events.Single();
            evt.TenantId.Should().Be(TenantA);
            var tags = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(evt.Tags)!;
            tags["ownerTenantId"].GetString().Should().Be(TenantA.ToString());
            tags["mode"].GetString().Should().Be("saas");
            tags.Should().NotContainKey("ownerUserId");
        }
    }

    [Test]
    public async Task CreateAsync_PrivateUserAgent_EmitsEvent_SingleUserMode()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            await repo.CreateAsync(NewUserAgent(), ValidConfig, null, null);

            var tags = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                events.Events.Single().Tags)!;
            tags["ownerUserId"].GetString().Should().Be(UserA.ToString());
            tags["mode"].GetString().Should().Be("single-user");
            tags.Should().NotContainKey("ownerTenantId");
        }
    }

    [Test]
    public async Task CreateAsync_EmptyConfig_Throws_NoRow_NoEvent()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var act = async () => await repo.CreateAsync(NewPublicAgent(), "  ", null, null);
            await act.Should().ThrowAsync<TammaError>();

            events.Events.Should().BeEmpty();
            (await ctx.Agents.CountAsync()).Should().Be(0);
        }
    }

    // ── Ownership guard (entity-level, before the DB CHECK) ──

    [Test]
    public async Task CreateAsync_PublicWithOwner_Throws_OwnershipGuard()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var bad = NewPublicAgent();
            bad.OwnerTenantId = TenantA;
            var act = async () => await repo.CreateAsync(bad, ValidConfig, null, null);

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.OWNERSHIP.PUBLIC_WITH_OWNER");
            events.Events.Should().BeEmpty();
            (await ctx.Agents.CountAsync()).Should().Be(0);
        }
    }

    [Test]
    public async Task CreateAsync_PrivateWithNoOwner_Throws_OwnershipGuard()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var bad = new Agent { Name = "x", Role = "architect", Visibility = AgentVisibility.Private };
            var act = async () => await repo.CreateAsync(bad, ValidConfig, null, null);

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.OWNERSHIP.PRIVATE_PRINCIPAL");
            events.Events.Should().BeEmpty();
        }
    }

    [Test]
    public async Task CreateAsync_PrivateWithBothOwners_Throws_OwnershipGuard()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var bad = new Agent
            {
                Name = "x", Role = "architect", Visibility = AgentVisibility.Private,
                OwnerTenantId = TenantA, OwnerUserId = UserA,
            };
            var act = async () => await repo.CreateAsync(bad, ValidConfig, null, null);

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.OWNERSHIP.PRIVATE_PRINCIPAL");
            events.Events.Should().BeEmpty();
        }
    }

    // ── PublishVersionAsync ──

    [Test]
    public async Task PublishVersionAsync_Increments_Monotonically_1_2_3()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var agent = await repo.CreateAsync(NewPublicAgent(), ValidConfig, null, null);

            var v2 = await repo.PublishVersionAsync(agent.Id, ValidConfig, "v2", null);
            var v3 = await repo.PublishVersionAsync(agent.Id, ValidConfig, "v3", null);

            v2!.Version.Should().Be(2);
            v3!.Version.Should().Be(3);

            var versions = await repo.ListVersionsAsync(agent.Id);
            versions.Select(v => v.Version).Should().Equal(1, 2, 3);

            // Pointer follows the highest version.
            var reloaded = await repo.GetByIdAsync(agent.Id);
            reloaded!.CurrentVersionId.Should().Be(v3.Id);

            // One create + two publish events.
            events.Events.Select(e => e.Type).Should().Equal(
                "AGENT.CREATED.SUCCESS",
                "AGENT.VERSION_PUBLISHED.SUCCESS",
                "AGENT.VERSION_PUBLISHED.SUCCESS");
        }
    }

    [Test]
    public async Task PublishVersionAsync_PriorVersions_RemainFetchable()
    {
        var (repo, _, ctx) = BuildRepo();
        await using (ctx)
        {
            var agent = await repo.CreateAsync(
                NewPublicAgent(), """{ "model": "v1" }""", null, null);
            await repo.PublishVersionAsync(agent.Id, """{ "model": "v2" }""", null, null);

            var v1 = await repo.GetVersionAsync(agent.Id, 1);
            v1!.ConfigJson.Should().Contain("v1", "the immutable v1 snapshot is untouched");
        }
    }

    [Test]
    public async Task RollbackPointer_RepointToOlderVersion_LeavesAllVersionsIntact()
    {
        var (repo, _, ctx) = BuildRepo();
        await using (ctx)
        {
            var agent = await repo.CreateAsync(NewPublicAgent(), """{ "model": "v1" }""", null, null);
            await repo.PublishVersionAsync(agent.Id, """{ "model": "v2" }""", null, null);

            // Simulate a rollback: repoint CurrentVersionId back to v1.
            var v1 = await repo.GetVersionAsync(agent.Id, 1);
            var entity = await ctx.Agents.FirstAsync(a => a.Id == agent.Id);
            entity.CurrentVersionId = v1!.Id;
            await ctx.SaveChangesAsync();

            // All versions remain.
            (await repo.ListVersionsAsync(agent.Id)).Should().HaveCount(2);
            var reloaded = await repo.GetByIdAsync(agent.Id);
            reloaded!.CurrentVersionId.Should().Be(v1.Id);
        }
    }

    [Test]
    public async Task PublishVersionAsync_UnknownAgent_ReturnsNull_NoEvent()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var result = await repo.PublishVersionAsync(Guid.NewGuid(), ValidConfig, null, null);
            result.Should().BeNull();
            events.Events.Should().BeEmpty();
        }
    }

    [Test]
    public async Task PublishVersionAsync_ConcurrentDoublePublish_IsMonotonic_NoDuplicate()
    {
        // Seed once.
        Guid agentId;
        await using (var seed = NewContext())
        {
            var repo = new AgentRepository(seed, new CapturingEventRepository());
            var agent = await repo.CreateAsync(NewPublicAgent(), ValidConfig, null, null);
            agentId = agent.Id;
        }

        // Two repositories (each its own CP context) race to publish v2.
        await using var ctxA = NewContext();
        await using var ctxB = NewContext();
        var repoA = new AgentRepository(ctxA, new CapturingEventRepository());
        var repoB = new AgentRepository(ctxB, new CapturingEventRepository());

        var taskA = repoA.PublishVersionAsync(agentId, ValidConfig, "A", null);
        var taskB = repoB.PublishVersionAsync(agentId, ValidConfig, "B", null);
        var results = await Task.WhenAll(taskA, taskB);

        // Both succeed (retry resolves the (AgentId, Version) collision); the
        // two published versions are distinct and monotonic.
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        var versions = results.Select(r => r!.Version).OrderBy(v => v).ToArray();
        versions.Should().Equal(2, 3);

        await using var verify = NewContext();
        var rows = await verify.AgentVersions
            .Where(v => v.AgentId == agentId).Select(v => v.Version).ToListAsync();
        rows.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        rows.Should().OnlyHaveUniqueItems("the unique index forbids duplicate versions");
    }

    // ── ArchiveAsync ──

    [Test]
    public async Task ArchiveAsync_SetsArchived_EmitsOneEvent()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var agent = await repo.CreateAsync(NewPublicAgent(), ValidConfig, null, null);
            events.Clear();

            var archived = await repo.ArchiveAsync(agent.Id, null);

            archived!.Status.Should().Be(AgentStatus.Archived);
            events.Events.Should().ContainSingle()
                .Which.Type.Should().Be("AGENT.ARCHIVED.SUCCESS");
        }
    }

    [Test]
    public async Task ArchiveAsync_AlreadyArchived_IsNoop_NoSecondEvent()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var agent = await repo.CreateAsync(NewPublicAgent(), ValidConfig, null, null);
            await repo.ArchiveAsync(agent.Id, null);
            events.Clear();

            var again = await repo.ArchiveAsync(agent.Id, null);

            again!.Status.Should().Be(AgentStatus.Archived);
            events.Events.Should().BeEmpty(
                "archiving an already-archived agent is a no-op — no second event");
        }
    }

    [Test]
    public async Task ArchiveAsync_UnknownAgent_ReturnsNull_NoEvent()
    {
        var (repo, events, ctx) = BuildRepo();
        await using (ctx)
        {
            var result = await repo.ArchiveAsync(Guid.NewGuid(), null);
            result.Should().BeNull();
            events.Events.Should().BeEmpty();
        }
    }

    // ── ListVisibleAsync ──

    [Test]
    public async Task ListVisibleAsync_Returns_Public_Union_OwnPrivate_NeverOthers()
    {
        var tenantB = Guid.NewGuid();
        await using (var setup = NewContext())
        {
            var repo = new AgentRepository(setup, new CapturingEventRepository());
            await repo.CreateAsync(NewPublicAgent("tamma-architect", "architect"), ValidConfig, null, null);
            await repo.CreateAsync(NewTenantAgent("atlas-a"), ValidConfig, null, null);
            await repo.CreateAsync(
                new Agent { Name = "atlas-b", Role = "architect", Visibility = AgentVisibility.Private, OwnerTenantId = tenantB },
                ValidConfig, null, null);
        }

        await using var ctx = NewContext();
        var repoR = new AgentRepository(ctx, new CapturingEventRepository());
        var visibleToA = await repoR.ListVisibleAsync(TenantA, null);

        var names = visibleToA.Select(a => a.Name).ToList();
        names.Should().Contain("tamma-architect");
        names.Should().Contain("atlas-a");
        names.Should().NotContain("atlas-b", "tenant A must never see tenant B's private agent");
    }

    /// <summary>
    /// Capturing <see cref="IEventRepository"/> — records appended events so
    /// tests can assert emission / no-emission / tags without a real tenant DB.
    /// </summary>
    private sealed class CapturingEventRepository : IEventRepository
    {
        private readonly ConcurrentQueue<DomainEvent> _events = new();
        public IReadOnlyList<DomainEvent> Events => _events.ToList();
        public void Clear() => _events.Clear();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            evt.CreatedAt = evt.CreatedAt == default ? DateTime.UtcNow : evt.CreatedAt;
            _events.Enqueue(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) =>
            Task.FromResult(_events.FirstOrDefault(e => e.Id == id));
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(_events.ToList());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult(_events.LastOrDefault(e => e.Type == type));
        public Task ClearAsync(Guid tenantId) { Clear(); return Task.CompletedTask; }
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)_events.ToList(), _events.Count));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)_events.ToList(), _events.Count));
    }
}
