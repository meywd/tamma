using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-2 — the entity-aware resolution chain (AC 2, 3, 9, 10, 13). Drives
/// <see cref="AgentResolverService.ResolveForRoleAsync"/> /
/// <see cref="AgentResolverService.ResolveForRoleAndPhaseAsync"/> against a real
/// Postgres testcontainer with the real <see cref="AgentRepository"/> +
/// <see cref="AgentSelectionRepository"/> + <see cref="AgentRegistryService"/>,
/// proving all four precedence branches including the no-empty-fallback throw.
/// </summary>
[TestFixture]
public class AgentResolverChainTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3202-3202-3202-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3202-3202-3202-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3202-3202-3202-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_resolve_chain_test")
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
            "TRUNCATE agent_role_selections, agent_versions, agents CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private const string Cfg = """{ "provider": "anthropic", "model": "claude-sonnet-4" }""";

    /// <summary>
    /// Builds the full resolver stack. The SaaS selection path routes through a
    /// tenant context — for these tests we point the tenant factory at the SAME
    /// physical DB (the per-tenant connection is the production isolation plane;
    /// here we validate the discriminator-column routing, mirroring how
    /// audit/prompt repos test the shared-DB transitional phase).
    /// </summary>
    private Harness BuildHarness(TammaMode mode, Guid? tenantId, Guid? userId)
    {
        var cpCtx = NewContext();
        var events = new CapturingEvents();
        var agentRepo = new AgentRepository(cpCtx, events);

        var tenantContext = new TenantContext();
        if (tenantId is Guid tid) tenantContext.SetTenantId(tid);

        var factory = new SameDbTenantFactory(_connectionString);
        var selectionRepo = new AgentSelectionRepository(cpCtx, factory, tenantContext);

        var modeProvider = new StubMode(mode);
        var httpAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = Principal(userId) },
        };

        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, modeProvider, tenantContext, httpAccessor,
            NullLogger<AgentRegistryService>.Instance);

        // The legacy JSONB repo is never exercised by the entity-aware chain; a
        // real instance is wired so the full constructor is satisfied.
        var legacyRepo = new AgentConfigRepository(factory);
        var resolver = new AgentResolverService(
            legacyRepo, null, NullLogger<AgentResolverService>.Instance,
            registry, agentRepo, events);

        return new Harness(resolver, registry, agentRepo, events, cpCtx);
    }

    private sealed record Harness(
        AgentResolverService Resolver,
        AgentRegistryService Registry,
        AgentRepository Agents,
        CapturingEvents Events,
        ControlPlaneDbContext Ctx);

    private static ClaimsPrincipal Principal(Guid? userId)
    {
        var claims = new List<Claim>();
        if (userId is Guid uid) claims.Add(new Claim(ClaimTypes.NameIdentifier, uid.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    // ── seed helpers ──

    private async Task<Agent> SeedPublicAsync(AgentRepository repo, string role, string handle)
        => await repo.CreateAsync(
            new Agent { Name = handle, Role = role, Visibility = AgentVisibility.Public },
            Cfg, "seed", null);

    private async Task<Agent> SeedTenantPrivateAsync(AgentRepository repo, Guid tenantId, string role, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = role, Visibility = AgentVisibility.Private, OwnerTenantId = tenantId },
            Cfg, "seed", null);

    private async Task<Agent> SeedUserPrivateAsync(AgentRepository repo, Guid userId, string role, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = role, Visibility = AgentVisibility.Private, OwnerUserId = userId },
            Cfg, "seed", null);

    // ── Branch (c): no selection ⇒ system-default public ──

    [Test]
    public async Task Resolve_NoSelection_FallsToSystemDefaultPublic()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            var pub = await SeedPublicAsync(h.Agents, "architect", "tamma-architect");

            var resolved = await h.Resolver.ResolveForRoleAsync("architect");

            resolved.Source.Should().Be("system-public");
            resolved.AgentId.Should().Be(pub.Id);
            resolved.AgentVersion.Should().Be(1);
            resolved.Role.Should().Be("architect");
            resolved.Provider.Should().NotBeNullOrEmpty();
        }
    }

    // ── Branch (b): tenant-selected public wins over system default ──

    [Test]
    public async Task Resolve_SelectedPublic_Wins_Source_TenantPublic()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            // Two public agents for the role; selecting one must override the
            // "first matching" system default.
            await SeedPublicAsync(h.Agents, "developer", "tamma-developer");
            var chosen = await SeedPublicAsync(h.Agents, "developer", "acme-public-dev");

            await h.Registry.SelectForRoleAsync("developer", chosen.Id, null);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.AgentId.Should().Be(chosen.Id);
            resolved.Source.Should().Be("tenant-public");
        }
    }

    // ── Branch (a): tenant-selected private wins over everything ──

    [Test]
    public async Task Resolve_SelectedPrivate_Wins_Source_TenantPrivate()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            await SeedPublicAsync(h.Agents, "developer", "tamma-developer");
            var priv = await SeedTenantPrivateAsync(h.Agents, TenantA, "developer", "atlas");

            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.AgentId.Should().Be(priv.Id);
            resolved.Source.Should().Be("tenant-private");
            resolved.Handle.Should().Be("atlas");
        }
    }

    // ── Branch (d): NO system default ⇒ fail loud, no blank config ──

    [Test]
    public async Task Resolve_NoSelection_NoSystemDefault_FailsLoud_NoBlankConfig()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            // No agent seeded for "tester" at all.
            Func<Task> act = async () => await h.Resolver.ResolveForRoleAsync("tester");

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.RESOLVE.NO_DEFAULT");

            // The mandatory AGENT.RESOLVE.FAILED event fired (even without a
            // missing-config recorder) — and NO config object was produced.
            h.Events.Captured.Should().ContainSingle(e => e.Type == "AGENT.RESOLVE.FAILED");
        }
    }

    [Test]
    public async Task Resolve_FailLoud_Event_CarriesRoleAndMode()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            try { await h.Resolver.ResolveForRoleAsync("security"); }
            catch (TammaError) { /* expected */ }

            var evt = h.Events.Captured.Single(e => e.Type == "AGENT.RESOLVE.FAILED");
            using var tags = JsonDocument.Parse(evt.Tags!);
            tags.RootElement.GetProperty("role").GetString().Should().Be("security");
            tags.RootElement.GetProperty("source").GetString().Should().Be("none");
            tags.RootElement.GetProperty("mode").GetString().Should().Be("saas");
        }
    }

    // ── Stale selection ⇒ degrade to system default (not error) ──

    [Test]
    public async Task Resolve_StaleArchivedSelection_DegradesToSystemDefault()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            var pub = await SeedPublicAsync(h.Agents, "developer", "tamma-developer");
            var priv = await SeedTenantPrivateAsync(h.Agents, TenantA, "developer", "atlas");
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            // Archive the selected private agent — the selection is now stale.
            await h.Agents.ArchiveAsync(priv.Id, null);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.AgentId.Should().Be(pub.Id, "stale selection degrades to system default");
            resolved.Source.Should().Be("system-public");
        }
    }

    // ── Rollback-to-prior-version resolution (AC 13) ──

    [Test]
    public async Task Resolve_AfterRollbackToPriorVersion_ReturnsPriorVersionConfig()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                """{ "provider": "anthropic", "model": "v1-model" }""", "v1", null);
            await h.Agents.PublishVersionAsync(
                priv.Id, """{ "provider": "openai", "model": "v2-model" }""", "v2", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            // Active is v2 now.
            var v2Resolved = await h.Resolver.ResolveForRoleAsync("developer");
            v2Resolved.AgentVersion.Should().Be(2);
            v2Resolved.Model.Should().Be("v2-model");

            // Rollback to v1.
            await h.Agents.SetActiveVersionAsync(priv.Id, 1, null);

            var v1Resolved = await h.Resolver.ResolveForRoleAsync("developer");
            v1Resolved.AgentVersion.Should().Be(1);
            v1Resolved.Model.Should().Be("v1-model");
        }
    }

    // ── Mode-parameterized principal (AC 10) ──

    [Test]
    public async Task Resolve_SingleUserMode_UsesUserPrincipal_PrivateSelection()
    {
        var h = BuildHarness(TammaMode.SingleUser, null, UserA);
        await using (h.Ctx)
        {
            await SeedPublicAsync(h.Agents, "developer", "tamma-developer");
            var priv = await SeedUserPrivateAsync(h.Agents, UserA, "developer", "myagent");
            await h.Registry.SelectForRoleAsync("developer", priv.Id, UserA);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.AgentId.Should().Be(priv.Id);
            resolved.Source.Should().Be("tenant-private");
        }
    }

    // ── Unknown role / phase → ArgumentException before resolution ──

    [Test]
    public async Task Resolve_UnknownRole_Throws_ArgumentException()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            Func<Task> act = async () => await h.Resolver.ResolveForRoleAsync("wizard");
            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Test]
    public async Task ResolveForPhase_IneligibleRole_Throws_BeforeResolution()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            Func<Task> act = async () =>
                await h.Resolver.ResolveForRoleAndPhaseAsync("plan-system-design", "tester");
            await act.Should().ThrowAsync<ArgumentException>();
            // No event should have been emitted on an invalid-input rejection.
            h.Events.Captured.Should().NotContain(e => e.Type == "AGENT.RESOLVE.FAILED");
        }
    }

    // ── Cross-tenant: selecting another tenant's private agent → not found ──

    [Test]
    public async Task SelectForRole_CrossTenantPrivateTarget_ThrowsNotFound_NoSelectionRow()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            // A private agent owned by TenantB — TenantA must not be able to select it.
            var foreign = await SeedTenantPrivateAsync(h.Agents, TenantB, "developer", "their-atlas");

            Func<Task> act = async () =>
                await h.Registry.SelectForRoleAsync("developer", foreign.Id, null);

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.SELECT.NOT_FOUND");

            var selections = await h.Registry.GetRoleSelectionsAsync();
            selections.Should().NotContainKey("developer");
        }
    }

    // ── Selection emits exactly one DCB event (AC 11) ──

    [Test]
    public async Task SelectForRole_EmitsOne_SelectedForRole_Event()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            var pub = await SeedPublicAsync(h.Agents, "developer", "tamma-developer");
            h.Events.Captured.Clear();

            await h.Registry.SelectForRoleAsync("developer", pub.Id, null);

            var selectEvents = h.Events.Captured
                .Where(e => e.Type == "AGENT.SELECTED_FOR_ROLE.SUCCESS").ToList();
            selectEvents.Should().HaveCount(1);
            using var tags = JsonDocument.Parse(selectEvents[0].Tags!);
            tags.RootElement.GetProperty("agentId").GetString().Should().Be(pub.Id.ToString());
            tags.RootElement.GetProperty("role").GetString().Should().Be("developer");
            tags.RootElement.GetProperty("source").GetString().Should().Be("system-public");
        }
    }

    // ── test doubles ──

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    /// <summary>Tenant factory that hands back a TenantDbContext bound to the
    /// same physical DB (shared-DB transitional phase). The selection rows are
    /// discriminated by tenant_id, mirroring how prompt/audit repos test.</summary>
    private sealed class SameDbTenantFactory(string conn) : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            var ctx = new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(conn).Options,
                tenantId);
            return ValueTask.FromResult(ctx);
        }
    }

    private sealed class CapturingEvents : IEventRepository
    {
        public ConcurrentQueue<DomainEvent> Captured { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Captured.Enqueue(evt);
            return Task.FromResult(evt);
        }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(Captured.ToList());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)Captured.ToList(), Captured.Count));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)Captured.ToList(), Captured.Count));
    }
}
