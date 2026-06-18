using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-1 (Task 4) — idempotency + correctness tests for
/// <see cref="AgentEntitySeeder"/>. The seeder creates one public agent per
/// canonical role with the <c>tamma-&lt;role&gt;</c> handle + a <c>Version=1</c>
/// snapshot, reusing the shipped config values. Re-running inserts nothing
/// (skip-by-existing-handle).
///
/// <para>Runs against a Postgres testcontainer applying the real CP migration,
/// so the partial unique index on public <c>(Name, Role)</c> is enforced — the
/// idempotency contract is proven structurally, not just by the EXISTS check.</para>
/// </summary>
[TestFixture]
public class AgentEntitySeederTests
{
    private Testcontainers.PostgreSql.PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new Testcontainers.PostgreSql.PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_seeder_test")
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

    [Test]
    public async Task FirstRun_Creates_OnePublicAgentPerRole_WithTammaHandles_AndVersion1()
    {
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        await using var verify = NewContext();
        var agents = await verify.Agents.ToListAsync();

        // One per canonical role (8 roles in the AgentRole taxonomy).
        agents.Should().HaveCount(RolePhaseMap.ValidRoles.Count);
        agents.Should().OnlyContain(a => a.Visibility == AgentVisibility.Public);
        agents.Should().OnlyContain(a => a.OwnerTenantId == null && a.OwnerUserId == null);

        // Handles are tamma-<role>, one per valid role.
        var names = agents.Select(a => a.Name).ToHashSet();
        foreach (var role in RolePhaseMap.ValidRoles)
        {
            names.Should().Contain($"tamma-{role}");
        }

        // Each agent has exactly one Version=1 snapshot, and CurrentVersionId
        // points at it.
        foreach (var a in agents)
        {
            var versions = await verify.AgentVersions
                .Where(v => v.AgentId == a.Id).ToListAsync();
            versions.Should().ContainSingle();
            versions[0].Version.Should().Be(1);
            a.CurrentVersionId.Should().Be(versions[0].Id);
        }
    }

    [Test]
    public async Task SecondRun_IsNoop_CountUnchanged()
    {
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        int agentCountAfterFirst;
        await using (var verify1 = NewContext())
        {
            agentCountAfterFirst = await verify1.Agents.CountAsync();
        }

        // Second run.
        await using (var ctx2 = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx2);
        }

        await using var verify2 = NewContext();
        var agentCountAfterSecond = await verify2.Agents.CountAsync();
        var versionCount = await verify2.AgentVersions.CountAsync();

        agentCountAfterSecond.Should().Be(agentCountAfterFirst,
            "re-running the seeder inserts nothing (skip-by-existing-handle)");
        versionCount.Should().Be(agentCountAfterFirst,
            "no duplicate Version=1 rows on re-run");
    }

    [Test]
    public async Task SeededConfigs_AllValidate()
    {
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        await using var verify = NewContext();
        var versions = await verify.AgentVersions.ToListAsync();

        versions.Should().NotBeEmpty();
        foreach (var v in versions)
        {
            var (valid, errors) = AgentConfigValidator.Validate(v.ConfigJson);
            valid.Should().BeTrue(
                "every seeded public-agent config must pass the saved-config validator; "
                + "errors: {0}", string.Join("; ", errors));
        }
    }

    [Test]
    public async Task SeededConfig_PreservesShippedValues()
    {
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        await using var verify = NewContext();
        var architect = await verify.Agents.FirstAsync(a => a.Name == "tamma-architect");
        var version = await verify.AgentVersions.FirstAsync(v => v.AgentId == architect.Id);

        using var doc = System.Text.Json.JsonDocument.Parse(version.ConfigJson);
        var root = doc.RootElement;
        // Shipped architect values from the legacy AgentSeeder: temperature 0.3,
        // maxTokens 4096, provider chain anthropic→openai→openrouter.
        root.GetProperty("temperature").GetDouble().Should().BeApproximately(0.3, 0.001);
        root.GetProperty("maxTokens").GetInt32().Should().Be(4096);
        root.GetProperty("providerChain").GetArrayLength().Should().BeGreaterThan(0);
    }
}
