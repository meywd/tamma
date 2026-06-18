using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-1 (Task 1) — Postgres-bound mapping tests for the
/// <see cref="Agent"/> / <see cref="AgentVersion"/> control-plane entities.
/// Applies the real <see cref="ControlPlaneDbContext"/> migration to a fresh
/// testcontainer so the <c>ck_agents_visibility_ownership</c> CHECK + partial
/// unique indexes physically exist, then asserts:
/// <list type="bullet">
///   <item>a valid public row and a valid private row insert cleanly;</item>
///   <item>the CHECK rejects public-with-owner, private-with-no-owner, and
///     private-with-both-owners on <c>SaveChanges</c>;</item>
///   <item>two tenants may each own a private agent named <c>atlas</c>
///     (the partial unique index does not collide across owners).</item>
/// </list>
///
/// <para>Why Postgres, not EF-InMemory: the InMemory provider does not enforce
/// CHECK constraints or partial unique indexes, so the invariant under test
/// would pass silently. A real Postgres connection is the only thing that
/// exposes a CHECK violation as <see cref="DbUpdateException"/>.</para>
/// </summary>
[TestFixture]
public class AgentEntityMappingTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3232-3232-3232-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3232-3232-3232-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3232-3232-3232-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_entity_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Apply the full CP migration bundle so the agents/agent_versions
        // tables exist with the CHECK + indexes exactly as production ships.
        await using var ctx = CreateContext();
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
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE agent_versions, agents CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    private ControlPlaneDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private static Agent NewAgent(
        string name,
        AgentVisibility visibility,
        Guid? ownerTenantId = null,
        Guid? ownerUserId = null,
        string role = "architect") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Role = role,
        Visibility = visibility,
        OwnerTenantId = ownerTenantId,
        OwnerUserId = ownerUserId,
        Status = AgentStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── Valid inserts ──

    [Test]
    public async Task Insert_PublicAgent_WithNoOwner_Succeeds()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent("tamma-architect", AgentVisibility.Public));

        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Insert_PrivateAgent_WithTenantOwner_Succeeds()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent("atlas", AgentVisibility.Private, ownerTenantId: TenantA));

        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Insert_PrivateAgent_WithUserOwner_Succeeds()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent("atlas", AgentVisibility.Private, ownerUserId: UserA));

        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    // ── CHECK rejections ──

    [Test]
    public async Task Insert_PublicAgent_WithOwner_IsRejectedByCheck()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent("bad-public", AgentVisibility.Public, ownerTenantId: TenantA));

        await AssertSqlStateAsync(ctx, PostgresErrorCodes.CheckViolation);
    }

    [Test]
    public async Task Insert_PrivateAgent_WithNoOwner_IsRejectedByCheck()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent("orphan-private", AgentVisibility.Private));

        await AssertSqlStateAsync(ctx, PostgresErrorCodes.CheckViolation);
    }

    [Test]
    public async Task Insert_PrivateAgent_WithBothOwners_IsRejectedByCheck()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent(
            "double-owner", AgentVisibility.Private,
            ownerTenantId: TenantA, ownerUserId: UserA));

        await AssertSqlStateAsync(ctx, PostgresErrorCodes.CheckViolation);
    }

    /// <summary>
    /// Save the context and assert it throws a <see cref="DbUpdateException"/>
    /// whose inner <see cref="PostgresException"/> carries
    /// <paramref name="expectedSqlState"/>. Captures the exception then asserts
    /// on the inner exception directly (FluentAssertions' fluent
    /// <c>WithInnerException</c> chaining is not available off an awaited
    /// <c>ThrowAsync</c> result).
    /// </summary>
    private static Task AssertSqlStateAsync(
        ControlPlaneDbContext ctx, string expectedSqlState)
    {
        var ex = Assert.CatchAsync<DbUpdateException>(
            async () => await ctx.SaveChangesAsync());
        ex.Should().NotBeNull();
        ex!.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(expectedSqlState);
        return Task.CompletedTask;
    }

    // ── Partial unique index: two tenants may each own a private "atlas" ──

    [Test]
    public async Task TwoTenants_MayEachOwn_PrivateAgent_NamedAtlas()
    {
        await using (var ctx = CreateContext())
        {
            ctx.Agents.Add(NewAgent("atlas", AgentVisibility.Private, ownerTenantId: TenantA));
            ctx.Agents.Add(NewAgent("atlas", AgentVisibility.Private, ownerTenantId: TenantB));
            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().NotThrowAsync(
                "the private per-owner partial index keys on (OwnerTenantId, Name) "
                + "so two tenants' 'atlas' agents do not collide");
        }

        await using var verify = CreateContext();
        var count = await verify.Agents.CountAsync(a => a.Name == "atlas");
        count.Should().Be(2);
    }

    [Test]
    public async Task SameTenant_CannotOwn_TwoPrivateAgents_WithSameName()
    {
        await using var ctx = CreateContext();
        ctx.Agents.Add(NewAgent("atlas", AgentVisibility.Private, ownerTenantId: TenantA));
        ctx.Agents.Add(NewAgent("atlas", AgentVisibility.Private, ownerTenantId: TenantA));

        await AssertSqlStateAsync(ctx, PostgresErrorCodes.UniqueViolation);
    }

    // ── Version monotonicity guard (the (AgentId, Version) unique index) ──

    [Test]
    public async Task Duplicate_AgentVersion_IsRejectedByUniqueIndex()
    {
        var agentId = Guid.NewGuid();
        await using (var seed = CreateContext())
        {
            var agent = NewAgent("tamma-tester", AgentVisibility.Public, role: "tester");
            agent.Id = agentId;
            seed.Agents.Add(agent);
            seed.AgentVersions.Add(new AgentVersion
            {
                Id = Guid.NewGuid(), AgentId = agentId, Version = 1,
                ConfigJson = "{}", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = CreateContext();
        ctx.AgentVersions.Add(new AgentVersion
        {
            Id = Guid.NewGuid(), AgentId = agentId, Version = 1,
            ConfigJson = "{}", CreatedAt = DateTime.UtcNow,
        });

        await AssertSqlStateAsync(ctx, PostgresErrorCodes.UniqueViolation);
    }
}
