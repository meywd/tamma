using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-2 — DB-level constraint tests for <c>agent_role_selections</c>:
/// the principal-XOR CHECK and the UNIQUE NULLS NOT DISTINCT
/// <c>(TenantId, UserId, Role)</c> index. Runs against a real Postgres
/// testcontainer (the CHECK + nulls-not-distinct semantics are PG-specific).
/// </summary>
[TestFixture]
public class AgentRoleSelectionConstraintTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-32c1-32c1-32c1-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-32c1-32c1-32c1-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("cccccccc-32c1-32c1-32c1-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_sel_constraint_test")
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
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE agent_role_selections CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private static AgentRoleSelection Row(Guid? tenantId, Guid? userId, string role = "developer")
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Role = role,
            AgentId = Guid.NewGuid(),
            Visibility = "system-public",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    [Test]
    public async Task PrincipalXor_BothSet_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(TenantA, UserA));
        Func<Task> act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task PrincipalXor_NeitherSet_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(null, null));
        Func<Task> act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task PrincipalXor_TenantOnly_Accepted()
    {
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(TenantA, null));
        await ctx.SaveChangesAsync();
        (await ctx.AgentRoleSelections.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task PrincipalXor_UserOnly_Accepted()
    {
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(null, UserA));
        await ctx.SaveChangesAsync();
        (await ctx.AgentRoleSelections.CountAsync()).Should().Be(1);
    }

    [Test]
    public async Task Unique_DuplicateTenantRole_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(TenantA, null, "developer"));
        await ctx.SaveChangesAsync();

        await using var ctx2 = NewContext();
        ctx2.AgentRoleSelections.Add(Row(TenantA, null, "developer"));
        Func<Task> act = async () => await ctx2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("one selection per (tenant, role)");
    }

    [Test]
    public async Task Unique_NullsNotDistinct_UserHalf_DedupesOnNullTenant()
    {
        // Two user-keyed rows (tenant_id NULL both) for the same (user, role)
        // must collide — NULLS NOT DISTINCT treats the NULL tenant halves equal.
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(null, UserA, "developer"));
        await ctx.SaveChangesAsync();

        await using var ctx2 = NewContext();
        ctx2.AgentRoleSelections.Add(Row(null, UserA, "developer"));
        Func<Task> act = async () => await ctx2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task Unique_DifferentTenants_SameRole_BothAllowed()
    {
        await using var ctx = NewContext();
        ctx.AgentRoleSelections.Add(Row(TenantA, null, "developer"));
        ctx.AgentRoleSelections.Add(Row(TenantB, null, "developer"));
        await ctx.SaveChangesAsync();
        (await ctx.AgentRoleSelections.CountAsync()).Should().Be(2,
            "two tenants select independently for the same role");
    }
}
