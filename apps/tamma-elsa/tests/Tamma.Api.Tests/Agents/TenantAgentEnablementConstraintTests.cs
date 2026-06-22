using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-16 — DB-level constraint tests for <c>tenant_agent_enablements</c>:
/// the principal-XOR CHECK and the UNIQUE NULLS NOT DISTINCT
/// <c>(TenantId, UserId, AgentId)</c> index. Runs against a real Postgres
/// testcontainer (the CHECK + nulls-not-distinct semantics are PG-specific).
/// Mirrors <see cref="AgentRoleSelectionConstraintTests"/> byte-for-byte.
/// </summary>
[TestFixture]
public class TenantAgentEnablementConstraintTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3216-3216-3216-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3216-3216-3216-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3216-3216-3216-cccccccccccc");
    private static readonly Guid AgentX = Guid.Parse("dddddddd-3216-3216-3216-dddddddddddd");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_enablement_constraint_test")
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
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE tenant_agent_enablements CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private static TenantAgentEnablement Row(Guid? tenantId, Guid? userId, Guid? agentId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            AgentId = agentId ?? AgentX,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    [Test]
    public async Task PrincipalXor_BothSet_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(TenantA, UserA));
        Func<Task> act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task PrincipalXor_NeitherSet_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(null, null));
        Func<Task> act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task PrincipalXor_TenantOnly_Accepted()
    {
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(TenantA, null));
        await ctx.SaveChangesAsync();
        (await ctx.TenantAgentEnablements.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task PrincipalXor_UserOnly_Accepted()
    {
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(null, UserA));
        await ctx.SaveChangesAsync();
        (await ctx.TenantAgentEnablements.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task Unique_DuplicateTenantAgent_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(TenantA, null, AgentX));
        await ctx.SaveChangesAsync();

        await using var ctx2 = NewContext();
        ctx2.TenantAgentEnablements.Add(Row(TenantA, null, AgentX));
        Func<Task> act = async () => await ctx2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("one enablement row per (tenant, agent)");
    }

    [Test]
    public async Task Unique_NullsNotDistinct_UserHalf_DedupesOnNullTenant()
    {
        // Two user-keyed rows (tenant_id NULL both) for the same (user, agent)
        // must collide — NULLS NOT DISTINCT treats the NULL tenant halves equal.
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(null, UserA, AgentX));
        await ctx.SaveChangesAsync();

        await using var ctx2 = NewContext();
        ctx2.TenantAgentEnablements.Add(Row(null, UserA, AgentX));
        Func<Task> act = async () => await ctx2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task Unique_DifferentTenants_SameAgent_BothAllowed()
    {
        await using var ctx = NewContext();
        ctx.TenantAgentEnablements.Add(Row(TenantA, null, AgentX));
        ctx.TenantAgentEnablements.Add(Row(TenantB, null, AgentX));
        await ctx.SaveChangesAsync();
        (await ctx.TenantAgentEnablements.CountAsync()).Should().Be(2,
            "two tenants enable the same persona independently");
    }
}
