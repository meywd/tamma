using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-16 (AC10) — <see cref="TenantEnablementSeeder"/>: a fresh principal
/// gets the platform default persona enabled out of the box; the seeder is
/// insert-missing-only and NEVER reverts an explicit disable.
/// </summary>
[TestFixture]
public class TenantEnablementSeederTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3216-5eed-3216-aaaaaaaaaaaa");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3216-5eed-3216-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_enablement_seed_test")
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
    public async Task FreshTenant_GetsDefaultPersonaEnabled()
    {
        await using var ctx = NewContext();
        var claude = await SeedClaudeAsync(ctx);

        var inserted = await TenantEnablementSeeder.SeedDefaultPersonaAsync(
            ctx, "claude", tenantId: TenantA, userId: null);

        inserted.Should().BeTrue();
        var row = await ctx.TenantAgentEnablements.SingleAsync(r => r.TenantId == TenantA);
        row.AgentId.Should().Be(claude.Id);
        row.Enabled.Should().BeTrue();
        row.UserId.Should().BeNull("XOR — SaaS keys TenantId only");
    }

    [Test]
    public async Task FreshUser_SingleUser_GetsDefaultPersonaEnabled()
    {
        await using var ctx = NewContext();
        await SeedClaudeAsync(ctx);

        var inserted = await TenantEnablementSeeder.SeedDefaultPersonaAsync(
            ctx, "claude", tenantId: null, userId: UserA);

        inserted.Should().BeTrue();
        var row = await ctx.TenantAgentEnablements.SingleAsync(r => r.UserId == UserA);
        row.Enabled.Should().BeTrue();
        row.TenantId.Should().BeNull("XOR — single-user keys UserId only");
    }

    [Test]
    public async Task Rerun_DoesNotRevertExplicitDisable()
    {
        await using var ctx = NewContext();
        var claude = await SeedClaudeAsync(ctx);

        // First seed enables.
        await TenantEnablementSeeder.SeedDefaultPersonaAsync(ctx, "claude", TenantA, null);

        // Admin explicitly disables.
        var row = await ctx.TenantAgentEnablements.SingleAsync(r => r.TenantId == TenantA);
        row.Enabled = false;
        await ctx.SaveChangesAsync();

        // Re-run the seeder — must NOT revert the disable.
        var inserted = await TenantEnablementSeeder.SeedDefaultPersonaAsync(ctx, "claude", TenantA, null);

        inserted.Should().BeFalse("insert-missing-only — a row already exists");
        var reread = await ctx.TenantAgentEnablements.SingleAsync(r => r.TenantId == TenantA);
        reread.Enabled.Should().BeFalse("the explicit disable is preserved across re-seed");
        (await ctx.TenantAgentEnablements.CountAsync(r => r.AgentId == claude.Id)).Should().Be(1);
    }

    [Test]
    public async Task Rerun_Idempotent_NoSecondRow()
    {
        await using var ctx = NewContext();
        await SeedClaudeAsync(ctx);

        await TenantEnablementSeeder.SeedDefaultPersonaAsync(ctx, "claude", TenantA, null);
        var second = await TenantEnablementSeeder.SeedDefaultPersonaAsync(ctx, "claude", TenantA, null);

        second.Should().BeFalse();
        (await ctx.TenantAgentEnablements.CountAsync(r => r.TenantId == TenantA)).Should().Be(1);
    }

    [Test]
    public async Task MissingDefaultPersona_SkipsWithoutThrow()
    {
        await using var ctx = NewContext();
        // No persona seeded.
        var inserted = await TenantEnablementSeeder.SeedDefaultPersonaAsync(ctx, "claude", TenantA, null);

        inserted.Should().BeFalse("no live default persona ⇒ skip, no half-seeded row");
        (await ctx.TenantAgentEnablements.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task BothPrincipals_ThrowsXorViolation()
    {
        await using var ctx = NewContext();
        await SeedClaudeAsync(ctx);

        Func<Task> act = async () => await TenantEnablementSeeder.SeedDefaultPersonaAsync(
            ctx, "claude", tenantId: TenantA, userId: UserA);
        await act.Should().ThrowAsync<ArgumentException>();
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
