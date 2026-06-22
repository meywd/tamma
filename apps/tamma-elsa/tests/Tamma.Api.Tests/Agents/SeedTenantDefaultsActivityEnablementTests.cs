using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-16 (spec deviation #1 closure) — proves the SaaS fresh-tenant path
/// (<see cref="SeedTenantDefaultsActivity"/>, Step 7 of <c>CreateTenantWorkflow</c>)
/// seeds the platform <c>DefaultPersonaName</c> persona enabled for the freshly
/// provisioned tenant — mirroring the single-user <c>FreshUser_…</c> seed wired
/// in <c>EnsurePersonalTenantMiddleware</c>.
///
/// <para>The activity's <see cref="ActivityExecutionContext"/> requires the Elsa
/// runtime, so (as with the other lifecycle activities) we exercise the
/// directly-callable seed helper the activity delegates to —
/// <see cref="SeedTenantDefaultsActivity.SeedTenantDefaultPersonaAsync"/> — against
/// a real <see cref="ControlPlaneDbContext"/>.</para>
/// </summary>
[TestFixture]
public class SeedTenantDefaultsActivityEnablementTests
{
    private static readonly Guid FreshTenant = Guid.Parse("aaaaaaaa-3216-5ac2-3216-aaaaaaaaaaaa");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("seed_tenant_defaults_enablement_test")
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
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE tenant_agent_enablements, agent_versions, agents CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private const string Cfg = """{ "provider": "anthropic", "model": "claude-sonnet-4" }""";

    private async Task<Agent> SeedClaudeAsync(ControlPlaneDbContext ctx)
        => await new AgentRepository(ctx, new NoopEvents()).CreateAsync(
            new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

    [Test]
    public async Task FreshSaasTenant_GetsDefaultPersonaEnabled_TenantKeyed()
    {
        await using var ctx = NewContext();
        var claude = await SeedClaudeAsync(ctx);

        await SeedTenantDefaultsActivity.SeedTenantDefaultPersonaAsync(
            ctx, "claude", FreshTenant, NullLogger.Instance);

        var row = await ctx.TenantAgentEnablements.SingleAsync(r => r.TenantId == FreshTenant);
        row.AgentId.Should().Be(claude.Id);
        row.Enabled.Should().BeTrue("a fresh SaaS tenant is usable out of the box");
        row.UserId.Should().BeNull("XOR — SaaS keys TenantId only (mirrors the single-user FreshUser_ case)");
    }

    [Test]
    public async Task FreshSaasTenant_Seed_IsInsertMissingOnly_NonFatal_WhenDefaultPersonaMissing()
    {
        await using var ctx = NewContext();
        // No persona seeded — must NOT throw (a seed failure cannot abort tenant creation).
        Func<Task> act = async () => await SeedTenantDefaultsActivity.SeedTenantDefaultPersonaAsync(
            ctx, "claude", FreshTenant, NullLogger.Instance);

        await act.Should().NotThrowAsync("seeding is best-effort/non-fatal");
        (await ctx.TenantAgentEnablements.CountAsync()).Should().Be(0, "no half-seeded row");
    }

    private sealed class NoopEvents : IEventRepository
    {
        private readonly ConcurrentQueue<DomainEvent> _captured = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { _captured.Enqueue(evt); return Task.FromResult(evt); }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(_captured.ToList());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)_captured.ToList(), _captured.Count));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)_captured.ToList(), _captured.Count));
    }
}
