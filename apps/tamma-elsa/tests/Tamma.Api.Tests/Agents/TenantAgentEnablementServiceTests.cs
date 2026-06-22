using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-16 — service-level tests for <see cref="TenantAgentEnablementService"/>
/// against a real Postgres testcontainer with the real
/// <see cref="AgentRepository"/> catalog. Covers enable/disable upsert + events,
/// the <c>IsEnabledForPrincipalAsync</c> truth table, <c>ListEnabledPublicAgentIds</c>,
/// <c>GetEnabledDefaultPersonaId</c>, disable-own-private 409, 404 unseen, and
/// idempotency — parameterized over single-user / SaaS principal keying.
/// </summary>
[TestFixture]
public class TenantAgentEnablementServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3216-5e21-3216-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3216-5e21-3216-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3216-5e21-3216-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_enablement_svc_test")
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
            "TRUNCATE tenant_agent_enablements, agent_role_selections, agent_versions, agents CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private const string Cfg = """{ "provider": "anthropic", "model": "claude-sonnet-4" }""";

    private sealed record Svc(
        TenantAgentEnablementService Service,
        CapturingEvents Events,
        AgentRepository Agents,
        ControlPlaneDbContext Ctx,
        Principal Principal);

    /// <summary>Build the service for a principal, parameterized by mode.</summary>
    private Svc BuildService(
        TammaMode mode, Guid? tenantId, Guid? userId, string defaultPersonaName = "claude")
    {
        var ctx = NewContext();
        var events = new CapturingEvents();
        var agents = new AgentRepository(ctx, events);

        var tenantContext = new TenantContext();
        if (tenantId is Guid tid) tenantContext.SetTenantId(tid);

        var claims = new List<Claim>();
        if (userId is Guid uid) claims.Add(new Claim(ClaimTypes.NameIdentifier, uid.ToString()));
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };

        var personaOptions = Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = defaultPersonaName });

        var svc = new TenantAgentEnablementService(
            ctx, agents, events, new StubMode(mode), tenantContext, http,
            personaOptions, NullLogger<TenantAgentEnablementService>.Instance);

        var principal = mode == TammaMode.SaaS
            ? Principal.ForTenant(tenantId)
            : Principal.ForUser(userId);

        return new Svc(svc, events, agents, ctx, principal);
    }

    private async Task<Agent> SeedPersonaAsync(AgentRepository repo, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

    private async Task<Agent> SeedTenantPrivateAsync(AgentRepository repo, Guid tenantId, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = tenantId },
            Cfg, null, null);

    private async Task<Agent> SeedUserPrivateAsync(AgentRepository repo, Guid userId, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = "developer", Visibility = AgentVisibility.Private, OwnerUserId = userId },
            Cfg, null, null);

    // ── enable/disable + events ──

    [Test]
    public async Task EnableAsync_PublicPersona_CreatesRow_And_EmitsOneEvent()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");

            var state = await s.Service.EnableAsync(claude.Id);
            state.Enabled.Should().BeTrue();
            state.ImplicitlyEnabled.Should().BeFalse();
            state.PersonaName.Should().Be("claude");

            (await s.Ctx.TenantAgentEnablements.CountAsync(
                r => r.TenantId == TenantA && r.AgentId == claude.Id && r.Enabled)).Should().Be(1);

            var evts = s.Events.OfType(AgentEnablementEventTypes.Enabled);
            evts.Should().ContainSingle("exactly one AGENT.ENABLED.SUCCESS per enable");
            var tags = Tags(evts.Single());
            tags["agentId"].Should().Be(claude.Id.ToString());
            tags["personaName"].Should().Be("claude");
            tags["mode"].Should().Be("saas");
            tags["tenantId"].Should().Be(TenantA.ToString());
            tags.Should().NotContainKey("userId");
        }
    }

    [Test]
    public async Task EnableAsync_ConcurrentFirstTimeInsert_LoserUpdates_NoUnhandledDbUpdateException()
    {
        // Pre-stage a public persona via an independent context so BOTH racing
        // services see it in the catalog.
        Guid claudeId;
        await using (var seedCtx = NewContext())
        {
            var claude = await SeedPersonaAsync(new AgentRepository(seedCtx, new CapturingEvents()), "claude");
            claudeId = claude.Id;
        }

        // Two independent services (independent contexts/connections) for the SAME
        // principal+agent — the exact shape of two concurrent first-time enable calls.
        var a = BuildService(TammaMode.SaaS, TenantA, null);
        var b = BuildService(TammaMode.SaaS, TenantA, null);
        await using (a.Ctx)
        await using (b.Ctx)
        {
            // Both read null (empty table), both attempt the first-time INSERT; the
            // unique (TenantId, UserId, AgentId) index makes exactly one win. The
            // loser MUST recover via the unique-violation catch (detach → re-read →
            // UPDATE), NOT escape as an unhandled DbUpdateException.
            Func<Task> race = async () => await Task.WhenAll(
                a.Service.EnableAsync(claudeId),
                b.Service.EnableAsync(claudeId));

            await race.Should().NotThrowAsync<DbUpdateException>(
                "the upsert race loser recovers to an UPDATE (no unhandled DbUpdateException)");

            await using var verify = NewContext();
            (await verify.TenantAgentEnablements.CountAsync(
                    r => r.TenantId == TenantA && r.AgentId == claudeId))
                .Should().Be(1, "exactly one enablement row survives the race");
            (await verify.TenantAgentEnablements.SingleAsync(
                    r => r.TenantId == TenantA && r.AgentId == claudeId))
                .Enabled.Should().BeTrue("both racers intended enabled=true");
        }
    }

    [Test]
    public async Task EnableAsync_Reenable_IsIdempotent_SingleRow()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);
            await s.Service.EnableAsync(claude.Id);

            (await s.Ctx.TenantAgentEnablements.CountAsync(r => r.AgentId == claude.Id))
                .Should().Be(1, "re-enable upserts the same single row");
        }
    }

    [Test]
    public async Task DisableAsync_PublicPersona_SetsFalse_And_EmitsOneEvent()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);
            s.Events.Reset();

            var state = await s.Service.DisableAsync(claude.Id);
            state.Enabled.Should().BeFalse();

            var row = await s.Ctx.TenantAgentEnablements
                .SingleAsync(r => r.TenantId == TenantA && r.AgentId == claude.Id);
            row.Enabled.Should().BeFalse();

            s.Events.OfType(AgentEnablementEventTypes.Disabled)
                .Should().ContainSingle("exactly one AGENT.DISABLED.SUCCESS per disable");
        }
    }

    [Test]
    public async Task DisableAsync_OwnPrivateAgent_Throws409Equivalent()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var atlas = await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas");

            Func<Task> act = async () => await s.Service.DisableAsync(atlas.Id);
            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.ENABLEMENT.PRIVATE_NOT_DISABLEABLE");

            (await s.Ctx.TenantAgentEnablements.CountAsync()).Should().Be(0, "no row written");
            s.Events.OfType(AgentEnablementEventTypes.Disabled).Should().BeEmpty();
        }
    }

    [Test]
    public async Task EnableAsync_OwnPrivateAgent_NoOpConfirm_NoRow_NoEvent()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var atlas = await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas");

            var state = await s.Service.EnableAsync(atlas.Id);
            state.Enabled.Should().BeTrue();
            state.ImplicitlyEnabled.Should().BeTrue();

            (await s.Ctx.TenantAgentEnablements.CountAsync()).Should().Be(0, "private agents need no row");
            s.Events.OfType(AgentEnablementEventTypes.Enabled).Should().BeEmpty();
        }
    }

    [Test]
    public async Task EnableAsync_UnseenTarget_Throws404Equivalent()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            // Cross-tenant private target — TenantA can't see TenantB's private agent.
            var foreign = await SeedTenantPrivateAsync(s.Agents, TenantB, "their-atlas");

            Func<Task> act = async () => await s.Service.EnableAsync(foreign.Id);
            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.ENABLEMENT.NOT_FOUND");

            // Also a wholly non-existent id.
            Func<Task> act2 = async () => await s.Service.EnableAsync(Guid.NewGuid());
            (await act2.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.ENABLEMENT.NOT_FOUND");
        }
    }

    // ── IsEnabledForPrincipalAsync truth table ──

    [Test]
    public async Task IsEnabledForPrincipal_TruthTable()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claudeEnabled = await SeedPersonaAsync(s.Agents, "claude");
            var geminiNoRow = await SeedPersonaAsync(s.Agents, "gemini");
            var codegptDisabled = await SeedPersonaAsync(s.Agents, "codegpt");
            var ownPriv = await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas");
            var foreignPriv = await SeedTenantPrivateAsync(s.Agents, TenantB, "their-atlas");

            await s.Service.EnableAsync(claudeEnabled.Id);
            await s.Service.EnableAsync(codegptDisabled.Id);
            await s.Service.DisableAsync(codegptDisabled.Id);

            var p = s.Principal;
            (await s.Service.IsEnabledForPrincipalAsync(claudeEnabled.Id, p))
                .Should().BeTrue("enabled public persona ⇒ true");
            (await s.Service.IsEnabledForPrincipalAsync(geminiNoRow.Id, p))
                .Should().BeFalse("no-row public persona ⇒ false (default-deny)");
            (await s.Service.IsEnabledForPrincipalAsync(codegptDisabled.Id, p))
                .Should().BeFalse("disabled public persona ⇒ false");
            (await s.Service.IsEnabledForPrincipalAsync(ownPriv.Id, p))
                .Should().BeTrue("own private/custom agent ⇒ implicitly true with no row");
            (await s.Service.IsEnabledForPrincipalAsync(foreignPriv.Id, p))
                .Should().BeFalse("cross-scope private agent ⇒ false");
            (await s.Service.IsEnabledForPrincipalAsync(Guid.NewGuid(), p))
                .Should().BeFalse("non-existent agent ⇒ false");
        }
    }

    [Test]
    public async Task IsEnabledForPrincipal_RetiredEnabledPersona_IsFalse()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);

            // Archive the persona platform-wide; the stale enabled row resolves out.
            await s.Agents.ArchiveAsync(claude.Id, null);

            (await s.Service.IsEnabledForPrincipalAsync(claude.Id, s.Principal))
                .Should().BeFalse("a retired persona is not enabled even with a stale row");
        }
    }

    // ── ListEnabledPublicAgentIdsAsync ──

    [Test]
    public async Task ListEnabledPublicAgentIds_ReturnsOnlyEnabledPublic()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            var gemini = await SeedPersonaAsync(s.Agents, "gemini");
            var codegpt = await SeedPersonaAsync(s.Agents, "codegpt");
            await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas"); // private — excluded

            await s.Service.EnableAsync(claude.Id);
            await s.Service.EnableAsync(gemini.Id);
            await s.Service.EnableAsync(codegpt.Id);
            await s.Service.DisableAsync(gemini.Id); // disabled — excluded

            var ids = await s.Service.ListEnabledPublicAgentIdsAsync(s.Principal);
            ids.Should().BeEquivalentTo(new[] { claude.Id, codegpt.Id });
        }
    }

    // ── GetEnabledDefaultPersonaIdAsync ──

    [Test]
    public async Task GetEnabledDefaultPersona_ConfiguredDefaultEnabled_ReturnsIt()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null, defaultPersonaName: "claude");
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            var gemini = await SeedPersonaAsync(s.Agents, "gemini");
            await s.Service.EnableAsync(claude.Id);
            await s.Service.EnableAsync(gemini.Id);

            (await s.Service.GetEnabledDefaultPersonaIdAsync(s.Principal))
                .Should().Be(claude.Id, "the configured default persona wins when enabled");
        }
    }

    [Test]
    public async Task GetEnabledDefaultPersona_DefaultNotEnabled_SingleEnabled_ReturnsThatOne()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null, defaultPersonaName: "claude");
        await using (s.Ctx)
        {
            await SeedPersonaAsync(s.Agents, "claude"); // configured default, NOT enabled
            var gemini = await SeedPersonaAsync(s.Agents, "gemini");
            await s.Service.EnableAsync(gemini.Id);

            (await s.Service.GetEnabledDefaultPersonaIdAsync(s.Principal))
                .Should().Be(gemini.Id, "the single enabled persona when the default is not enabled");
        }
    }

    [Test]
    public async Task GetEnabledDefaultPersona_NothingEnabled_ReturnsNull()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null, defaultPersonaName: "claude");
        await using (s.Ctx)
        {
            await SeedPersonaAsync(s.Agents, "claude");
            await SeedPersonaAsync(s.Agents, "gemini");

            (await s.Service.GetEnabledDefaultPersonaIdAsync(s.Principal))
                .Should().BeNull("nothing enabled ⇒ null");
        }
    }

    [Test]
    public async Task GetEnabledDefaultPersona_Ambiguous_ReturnsNull()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null, defaultPersonaName: "claude");
        await using (s.Ctx)
        {
            await SeedPersonaAsync(s.Agents, "claude"); // default NOT enabled
            var gemini = await SeedPersonaAsync(s.Agents, "gemini");
            var codegpt = await SeedPersonaAsync(s.Agents, "codegpt");
            await s.Service.EnableAsync(gemini.Id);
            await s.Service.EnableAsync(codegpt.Id);

            (await s.Service.GetEnabledDefaultPersonaIdAsync(s.Principal))
                .Should().BeNull("multiple enabled and none is the configured default ⇒ ambiguous ⇒ null");
        }
    }

    [Test]
    public async Task GetEnabledDefaultPersona_IsPureRead_NoWritesOrEvents()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null, defaultPersonaName: "claude");
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);
            s.Events.Reset();

            await s.Service.GetEnabledDefaultPersonaIdAsync(s.Principal);

            s.Events.All().Should().BeEmpty("the default-persona read emits no events");
            (await s.Ctx.TenantAgentEnablements.CountAsync()).Should().Be(1, "and writes no rows");
        }
    }

    // ── ListAsync catalog view ──

    [Test]
    public async Task ListAsync_ShowsPublicFlags_And_PrivateImplicit()
    {
        var s = BuildService(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await SeedPersonaAsync(s.Agents, "gemini");
            var atlas = await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas");
            await s.Service.EnableAsync(claude.Id);

            var view = await s.Service.ListAsync();

            view.Single(v => v.AgentId == claude.Id).Enabled.Should().BeTrue();
            view.Single(v => v.AgentId == claude.Id).ImplicitlyEnabled.Should().BeFalse();
            view.Single(v => v.PersonaName == "gemini").Enabled.Should().BeFalse();
            var priv = view.Single(v => v.AgentId == atlas.Id);
            priv.Enabled.Should().BeTrue();
            priv.ImplicitlyEnabled.Should().BeTrue();
        }
    }

    // ── mode-parameterized principal keying (single-user vs SaaS) ──

    [Test]
    public async Task EnableAsync_ModeParameterized_KeysCorrectColumn(
        [Values(TammaMode.SingleUser, TammaMode.SaaS)] TammaMode mode)
    {
        var tenantId = mode == TammaMode.SaaS ? TenantA : (Guid?)null;
        var userId = mode == TammaMode.SingleUser ? UserA : (Guid?)null;
        var s = BuildService(mode, tenantId, userId);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);

            var row = await s.Ctx.TenantAgentEnablements.SingleAsync(r => r.AgentId == claude.Id);
            if (mode == TammaMode.SaaS)
            {
                row.TenantId.Should().Be(TenantA);
                row.UserId.Should().BeNull("XOR — only TenantId is set in SaaS");
            }
            else
            {
                row.UserId.Should().Be(UserA);
                row.TenantId.Should().BeNull("XOR — only UserId is set in single-user");
            }

            // Event tags the correct principal.
            var tags = Tags(s.Events.OfType(AgentEnablementEventTypes.Enabled).Single());
            tags["mode"].Should().Be(mode == TammaMode.SaaS ? "saas" : "single-user");
            if (mode == TammaMode.SaaS)
            {
                tags["tenantId"].Should().Be(TenantA.ToString());
                tags.Should().NotContainKey("userId");
            }
            else
            {
                tags["userId"].Should().Be(UserA.ToString());
                tags.Should().NotContainKey("tenantId");
            }
        }
    }

    [Test]
    public async Task IsEnabledForPrincipal_SingleUser_OwnPrivate_Implicit()
    {
        var s = BuildService(TammaMode.SingleUser, null, UserA);
        await using (s.Ctx)
        {
            var ownPriv = await SeedUserPrivateAsync(s.Agents, UserA, "atlas");
            (await s.Service.IsEnabledForPrincipalAsync(ownPriv.Id, s.Principal))
                .Should().BeTrue("the sole user's own private agent is implicitly enabled");
        }
    }

    // ── helpers ──

    private static Dictionary<string, string?> Tags(DomainEvent evt)
        => JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags)!;

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private sealed class CapturingEvents : IEventRepository
    {
        private readonly ConcurrentQueue<DomainEvent> _captured = new();

        public IReadOnlyList<DomainEvent> All() => _captured.ToList();
        public IReadOnlyList<DomainEvent> OfType(string type)
            => _captured.Where(e => e.Type == type).ToList();
        public void Reset() { while (_captured.TryDequeue(out _)) { } }

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
