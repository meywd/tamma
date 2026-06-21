using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-15 — idempotency + correctness tests for the rewritten
/// <see cref="AgentEntitySeeder"/>. The seeder creates the named cross-role
/// public PERSONAS (claude/gemini/codegpt), each <c>Visibility='public'</c>,
/// <c>Role=NULL</c>, explicit <c>provider</c>+<c>model</c>, NO prompts, and a
/// <c>Version=1</c> snapshot. Re-running inserts nothing (skip-by-existing-name)
/// and never reverts an admin edit; an unpriceable persona is skipped.
///
/// <para>Runs against a Postgres testcontainer applying the real CP migration,
/// so the partial unique index on public <c>(Name)</c> is enforced and the price
/// book (Story 34-11) is real — the cost-basis guard is proven structurally.</para>
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
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE agent_versions, agents, provider_model_prices, providers CASCADE;");
        // The persona seeder's cost-basis guard (Story 34-11) needs the price
        // book populated — seed it before each test (mirrors the production
        // ordering: pricing seeder runs before the persona seeder).
        await ProviderPricingSeeder.SeedAsync(ctx);
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private static readonly string[] s_expectedPersonas = ["claude", "gemini", "codegpt"];

    [Test]
    public async Task FirstRun_Creates_NamedCrossRolePersonas_RoleNull_ExplicitModel_NoPrompts()
    {
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        await using var verify = NewContext();
        var agents = await verify.Agents.ToListAsync();

        agents.Should().HaveCount(s_expectedPersonas.Length);
        agents.Should().OnlyContain(a => a.Visibility == AgentVisibility.Public);
        agents.Should().OnlyContain(a => a.OwnerTenantId == null && a.OwnerUserId == null);
        // Cross-role personas — Role is NULL.
        agents.Should().OnlyContain(a => a.Role == null);

        var names = agents.Select(a => a.Name).ToHashSet();
        names.Should().BeEquivalentTo(s_expectedPersonas);
        // No legacy tamma-<role> rows produced.
        names.Should().NotContain(n => n.StartsWith("tamma-"));

        foreach (var a in agents)
        {
            var versions = await verify.AgentVersions
                .Where(v => v.AgentId == a.Id).ToListAsync();
            versions.Should().ContainSingle();
            versions[0].Version.Should().Be(1);
            a.CurrentVersionId.Should().Be(versions[0].Id);

            using var doc = JsonDocument.Parse(versions[0].ConfigJson);
            var root = doc.RootElement;
            // Explicit provider + model (no longer leaning on DefaultAgentConfig).
            root.GetProperty("provider").GetString().Should().NotBeNullOrEmpty();
            root.GetProperty("model").GetString().Should().NotBeNullOrEmpty();
            // Personas are prompt-free.
            root.TryGetProperty("prompts", out _).Should().BeFalse();
            root.TryGetProperty("systemPrompt", out _).Should().BeFalse();
        }

        // Each persona's explicit (provider, model).
        var claude = agents.Single(a => a.Name == "claude");
        var claudeCfg = JsonDocument.Parse(
            (await verify.AgentVersions.FirstAsync(v => v.AgentId == claude.Id)).ConfigJson);
        claudeCfg.RootElement.GetProperty("provider").GetString().Should().Be("anthropic");
        claudeCfg.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-4-20250514");

        var gemini = agents.Single(a => a.Name == "gemini");
        var geminiCfg = JsonDocument.Parse(
            (await verify.AgentVersions.FirstAsync(v => v.AgentId == gemini.Id)).ConfigJson);
        geminiCfg.RootElement.GetProperty("provider").GetString().Should().Be("google");
        geminiCfg.RootElement.GetProperty("model").GetString().Should().Be("gemini-1.5-pro");

        var codegpt = agents.Single(a => a.Name == "codegpt");
        var codegptCfg = JsonDocument.Parse(
            (await verify.AgentVersions.FirstAsync(v => v.AgentId == codegpt.Id)).ConfigJson);
        codegptCfg.RootElement.GetProperty("provider").GetString().Should().Be("openai");
        codegptCfg.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o");
    }

    [Test]
    public async Task SecondRun_IsNoop_CountUnchanged_NoCreatedEvent()
    {
        var events = new CapturingEvents();
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx, events);
        }

        int firstCount;
        await using (var verify1 = NewContext())
        {
            firstCount = await verify1.Agents.CountAsync();
        }
        events.Captured.Count(e => e.Type == "AGENT.CREATED.SUCCESS")
            .Should().Be(firstCount, "one created event per newly-seeded persona");

        // Second run — fresh event sink to prove no event fires on a skip.
        var events2 = new CapturingEvents();
        await using (var ctx2 = NewContext())
        {
            var inserted = await AgentEntitySeeder.SeedAsync(ctx2, events2);
            inserted.Should().Be(0, "re-run inserts nothing (skip-by-existing-name)");
        }

        await using var verify2 = NewContext();
        (await verify2.Agents.CountAsync()).Should().Be(firstCount);
        (await verify2.AgentVersions.CountAsync()).Should().Be(firstCount,
            "no duplicate Version=1 rows on re-run");
        events2.Captured.Should().NotContain(e => e.Type == "AGENT.CREATED.SUCCESS",
            "no AGENT.CREATED.SUCCESS is emitted for a skipped (already-existing) persona");
    }

    [Test]
    public async Task SecondRun_NeverReverts_AdminEditedPersona()
    {
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        // Admin publishes Version=2 for claude.
        Guid claudeId;
        await using (var edit = NewContext())
        {
            var claude = await edit.Agents.FirstAsync(a => a.Name == "claude");
            claudeId = claude.Id;
            var v2 = new AgentVersion
            {
                Id = Guid.NewGuid(),
                AgentId = claude.Id,
                Version = 2,
                ConfigJson = """{ "provider": "anthropic", "model": "claude-opus-4-20250514" }""",
                Notes = "admin edit",
                CreatedAt = DateTime.UtcNow,
            };
            edit.AgentVersions.Add(v2);
            claude.CurrentVersionId = v2.Id;
            await edit.SaveChangesAsync();
        }

        // Re-run the seeder — must leave the admin edit intact.
        await using (var ctx2 = NewContext())
        {
            (await AgentEntitySeeder.SeedAsync(ctx2)).Should().Be(0);
        }

        await using var verify = NewContext();
        var claudeAfter = await verify.Agents.FirstAsync(a => a.Id == claudeId);
        var active = await verify.AgentVersions.FirstAsync(v => v.Id == claudeAfter.CurrentVersionId);
        active.Version.Should().Be(2, "the seeder must not revert an admin-published version");
        active.ConfigJson.Should().Contain("claude-opus-4-20250514");
    }

    [Test]
    public async Task UnpriceablePersona_IsSkipped_NoHalfSeededRow()
    {
        // Remove the google price rows so the gemini persona's (provider, model)
        // is not IsKnown — it must be WARN-skipped (no half-seeded row), while
        // the priced personas still seed.
        await using (var prep = NewContext())
        {
            await prep.Database.ExecuteSqlRawAsync(
                "DELETE FROM provider_model_prices WHERE \"ProviderKey\" = 'google';");
        }

        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx);
        }

        await using var verify = NewContext();
        var names = (await verify.Agents.Select(a => a.Name).ToListAsync()).ToHashSet();
        names.Should().Contain("claude").And.Contain("codegpt");
        names.Should().NotContain("gemini", "an unpriceable persona is skipped");
        // No orphaned version row for the skipped persona.
        (await verify.AgentVersions.CountAsync())
            .Should().Be(names.Count, "no half-seeded version row for the skipped persona");
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
                "every seeded persona config must pass the saved-config validator; "
                + "errors: {0}", string.Join("; ", errors));
        }
    }

    [Test]
    public async Task LegacyTammaRoleRows_AreArchived_Idempotently_WithSingleEvent()
    {
        // Pre-seed a legacy tamma-<role> public row (as 32-1 would have on main).
        Guid legacyId = Guid.NewGuid();
        await using (var prep = NewContext())
        {
            prep.Agents.Add(new Agent
            {
                Id = legacyId,
                Name = "tamma-architect",
                Role = "architect",
                Visibility = AgentVisibility.Public,
                Status = AgentStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await prep.SaveChangesAsync();
        }

        var events = new CapturingEvents();
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx, events);
        }

        await using (var verify = NewContext())
        {
            var legacy = await verify.Agents.FirstAsync(a => a.Id == legacyId);
            legacy.Status.Should().Be(AgentStatus.Archived,
                "legacy tamma-<role> rows are archived (never destructive-deleted)");
        }
        events.Captured.Count(e => e.Type == "AGENT.ARCHIVED.SUCCESS")
            .Should().Be(1, "archiving emits exactly one event per legacy row");

        // Second run — idempotent: no second archive event.
        var events2 = new CapturingEvents();
        await using (var ctx2 = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx2, events2);
        }
        events2.Captured.Should().NotContain(e => e.Type == "AGENT.ARCHIVED.SUCCESS",
            "an already-archived legacy row is not re-archived");
    }

    [Test]
    public async Task CreatedEvent_CarriesPersonaShape_RoleNull_PlatformTenant()
    {
        var events = new CapturingEvents();
        await using (var ctx = NewContext())
        {
            await AgentEntitySeeder.SeedAsync(ctx, events);
        }

        var created = events.Captured.Where(e => e.Type == "AGENT.CREATED.SUCCESS").ToList();
        created.Should().HaveCount(s_expectedPersonas.Length);
        created.Should().OnlyContain(e => e.TenantId == null, "persona events are platform-feed");

        var claudeEvt = created.Single(e =>
        {
            using var t = JsonDocument.Parse(e.Tags);
            return t.RootElement.GetProperty("personaName").GetString() == "claude";
        });
        using var tags = JsonDocument.Parse(claudeEvt.Tags);
        tags.RootElement.GetProperty("visibility").GetString().Should().Be("public");
        tags.RootElement.GetProperty("role").ValueKind.Should().Be(JsonValueKind.Null);
        tags.RootElement.GetProperty("provider").GetString().Should().Be("anthropic");
        tags.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-4-20250514");
        tags.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task NullableRole_PublicPersona_RoundTrips_CheckPasses()
    {
        // AC1/AC13 — a Visibility=public, Role=NULL persona inserts (the
        // visibility-ownership CHECK still passes: public + no owners) and reads
        // back with Role null.
        var id = Guid.NewGuid();
        await using (var ctx = NewContext())
        {
            ctx.Agents.Add(new Agent
            {
                Id = id,
                Name = "claude",
                Role = null,
                Visibility = AgentVisibility.Public,
                Status = AgentStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var read = await verify.Agents.FirstAsync(a => a.Id == id);
        read.Role.Should().BeNull();
        read.Visibility.Should().Be(AgentVisibility.Public);
    }

    [Test]
    public async Task PublicNameIndex_RejectsDuplicateName_AcrossAnyRole()
    {
        // AC2/AC13 — two PUBLIC agents may not share a Name (even with different
        // or null roles) → IX_agents_public_name (the swapped index).
        await using (var ctx = NewContext())
        {
            ctx.Agents.Add(new Agent
            {
                Id = Guid.NewGuid(), Name = "claude", Role = null,
                Visibility = AgentVisibility.Public, Status = AgentStatus.Active,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = NewContext();
        ctx2.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(), Name = "claude", Role = "developer",
            Visibility = AgentVisibility.Public, Status = AgentStatus.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Func<Task> act = async () => await ctx2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "a second public agent with the same Name violates IX_agents_public_name");
    }

    [Test]
    public async Task PublicAndPrivate_SameName_Coexist()
    {
        // AC2/AC13 — a public 'claude' persona and a private 'claude' agent
        // coexist (private partial indexes are unchanged, scoped by owner).
        var tenantId = Guid.NewGuid();
        await using var ctx = NewContext();
        ctx.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(), Name = "claude", Role = null,
            Visibility = AgentVisibility.Public, Status = AgentStatus.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(), Name = "claude", Role = "developer",
            Visibility = AgentVisibility.Private, OwnerTenantId = tenantId,
            Status = AgentStatus.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Func<Task> act = async () => await ctx.SaveChangesAsync();
        await act.Should().NotThrowAsync(
            "public and private name spaces are disjoint (separate partial indexes)");
    }

    private sealed class CapturingEvents : IPlatformEventRepository
    {
        public ConcurrentQueue<PlatformEvent> Captured { get; } = new();

        public Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
        {
            Captured.Enqueue(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }

        public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PlatformEvent?>(null);

        public Task<IReadOnlyList<PlatformEvent>> QueryAsync(
            Guid? tenantId = null, Guid? userId = null, string? typePrefix = null,
            DateTime? since = null, bool includePlatformWide = false, int limit = 100,
            CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<PlatformEvent>)Captured.ToList());
    }
}
