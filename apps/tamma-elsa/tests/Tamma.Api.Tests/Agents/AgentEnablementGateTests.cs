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
/// Story 32-18 — the per-tenant ENABLEMENT GATE over the shipped 32-2/32-15
/// registry/resolver. Drives <see cref="AgentRegistryService"/> +
/// <see cref="AgentResolverService"/> against a real Postgres testcontainer with
/// a FAKED <see cref="ITenantAgentEnablementReader"/> (32-16) and a FAKED
/// <see cref="IPersonaPromptResolver"/> (32-15) — this story consumes those seams
/// and never re-implements them. Covers: the selection gate (enabled accepted /
/// disabled → 409 + AGENT.SELECT.NOT_ENABLED / own-private accepted / cross-tenant
/// 404), CanUseAsync truth table, resolution degrading past a disabled selection
/// (AGENT.RESOLVE.DEGRADED), the enabled-default lookup incl. fail-loud
/// (AGENT.RESOLVE.NO_ENABLED_DEFAULT), action plumbing, the mode matrix, and the
/// no-credential assertion.
/// </summary>
[TestFixture]
public class AgentEnablementGateTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3218-3218-3218-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3218-3218-3218-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("cccccccc-3218-3218-3218-cccccccccccc");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_enablement_gate_test")
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

    // ── harness ──

    private Harness BuildHarness(
        TammaMode mode, Guid? tenantId, Guid? userId,
        FakeEnablement enablement,
        string defaultPersonaName = "claude",
        IPersonaPromptResolver? personaPrompts = null,
        ICustomAgentPromptResolver? customPrompts = null)
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

        var personaOptions = Microsoft.Extensions.Options.Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = defaultPersonaName });

        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, modeProvider, tenantContext, httpAccessor,
            personaOptions, enablement, NullLogger<AgentRegistryService>.Instance);

        var legacyRepo = new AgentConfigRepository(factory);
        var resolver = new AgentResolverService(
            legacyRepo, null, NullLogger<AgentResolverService>.Instance,
            registry, agentRepo, events, null,
            personaPrompts ?? new CapturingPersonaPrompts("[PERSONA PROMPT]"),
            customPrompts ?? new CustomAgentPromptResolver(
                NullLogger<CustomAgentPromptResolver>.Instance));

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

    private async Task<Agent> SeedPersonaAsync(AgentRepository repo, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = null, Visibility = AgentVisibility.Public }, Cfg, "seed", null);

    private async Task<Agent> SeedTenantPrivateAsync(AgentRepository repo, Guid tenantId, string role, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = role, Visibility = AgentVisibility.Private, OwnerTenantId = tenantId },
            Cfg, "seed", null);

    private async Task<Agent> SeedUserPrivateAsync(AgentRepository repo, Guid userId, string role, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = role, Visibility = AgentVisibility.Private, OwnerUserId = userId },
            Cfg, "seed", null);

    // ── (1) Enablement gate on selection ──

    [Test]
    public async Task Select_EnabledPublicPersona_Succeeds()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var persona = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(persona.Id);
            h.Events.Captured.Clear();

            var sel = await h.Registry.SelectForRoleAsync("developer", persona.Id, null);

            sel.AgentId.Should().Be(persona.Id);
            h.Events.Captured.Should().ContainSingle(e => e.Type == "AGENT.SELECTED_FOR_ROLE.SUCCESS");
            h.Events.Captured.Should().NotContain(e => e.Type == "AGENT.SELECT.NOT_ENABLED");
        }
    }

    [Test]
    public async Task Select_DisabledPublicPersona_Throws_NotEnabled_EmitsEvent()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var persona = await SeedPersonaAsync(h.Agents, "claude");
            // NOT enabled.
            h.Events.Captured.Clear();

            Func<Task> act = async () => await h.Registry.SelectForRoleAsync("developer", persona.Id, null);

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.SELECT.NOT_ENABLED");

            h.Events.Captured.Where(e => e.Type == "AGENT.SELECT.NOT_ENABLED").Should().HaveCount(1);
            // No selection row was upserted.
            var selections = await h.Registry.GetRoleSelectionsAsync();
            selections.Should().NotContainKey("developer");
        }
    }

    [Test]
    public async Task Select_DisabledPersona_Event_CarriesPersonaNameAndRole()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var persona = await SeedPersonaAsync(h.Agents, "claude");
            h.Events.Captured.Clear();

            try { await h.Registry.SelectForRoleAsync("security", persona.Id, null); }
            catch (TammaError) { /* expected */ }

            var evt = h.Events.Captured.Single(e => e.Type == "AGENT.SELECT.NOT_ENABLED");
            using var tags = JsonDocument.Parse(evt.Tags!);
            tags.RootElement.GetProperty("agentId").GetString().Should().Be(persona.Id.ToString());
            tags.RootElement.GetProperty("personaName").GetString().Should().Be("claude");
            tags.RootElement.GetProperty("role").GetString().Should().Be("security");
            tags.RootElement.GetProperty("mode").GetString().Should().Be("saas");
        }
    }

    [Test]
    public async Task Select_OwnPrivateAgent_Succeeds_GateSkipped()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var priv = await SeedTenantPrivateAsync(h.Agents, TenantA, "developer", "atlas");

            var sel = await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            sel.AgentId.Should().Be(priv.Id);
            // own-private is implicitly enabled — the enablement reader is never
            // even consulted for the gate decision.
            enablement.IsEnabledCalls.Should().NotContain(priv.Id);
        }
    }

    [Test]
    public async Task Select_CrossTenantPrivateTarget_Returns_NotFound_Not409()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var foreign = await SeedTenantPrivateAsync(h.Agents, TenantB, "developer", "their-atlas");

            Func<Task> act = async () => await h.Registry.SelectForRoleAsync("developer", foreign.Id, null);

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.SELECT.NOT_FOUND");
        }
    }

    // ── (2) CanUseAsync truth table ──

    [Test]
    public async Task CanUseAsync_PublicEnabled_True_PublicDisabled_False()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var enabled = await SeedPersonaAsync(h.Agents, "claude");
            var disabled = await SeedPersonaAsync(h.Agents, "gemini");
            enablement.Enable(enabled.Id);

            (await h.Registry.CanUseAsync(enabled)).Should().BeTrue();
            (await h.Registry.CanUseAsync(disabled)).Should().BeFalse(
                "a public persona is NOT usable solely because it is public");
        }
    }

    [Test]
    public async Task CanUseAsync_OwnPrivate_True_OtherTenantPrivate_False()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement);
        await using (h.Ctx)
        {
            var own = await SeedTenantPrivateAsync(h.Agents, TenantA, "developer", "atlas");
            var other = await SeedTenantPrivateAsync(h.Agents, TenantB, "developer", "their-atlas");

            (await h.Registry.CanUseAsync(own)).Should().BeTrue();
            (await h.Registry.CanUseAsync(other)).Should().BeFalse();
        }
    }

    // ── (3) Resolution degrades past a disabled selection ──

    [Test]
    public async Task Resolve_SelectionDisabledAfterSelect_DegradesToEnabledDefault_EmitsDegraded()
    {
        var enablement = new FakeEnablement();
        var persona = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement,
            defaultPersonaName: "claude", personaPrompts: persona);
        await using (h.Ctx)
        {
            var def = await SeedPersonaAsync(h.Agents, "claude");
            var picked = await SeedPersonaAsync(h.Agents, "gemini");
            // Both enabled when selecting "gemini".
            enablement.Enable(def.Id);
            enablement.Enable(picked.Id);
            await h.Registry.SelectForRoleAsync("developer", picked.Id, null);

            // Now disable the selected persona — the stored selection is stale.
            enablement.Disable(picked.Id);
            h.Events.Captured.Clear();

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.AgentId.Should().Be(def.Id, "degrades to the enabled default, not the disabled selection");
            resolved.Source.Should().Be("system-public");
            h.Events.Captured.Should().ContainSingle(e => e.Type == "AGENT.RESOLVE.DEGRADED");
            var evt = h.Events.Captured.Single(e => e.Type == "AGENT.RESOLVE.DEGRADED");
            using var tags = JsonDocument.Parse(evt.Tags!);
            tags.RootElement.GetProperty("staleAgentId").GetString().Should().Be(picked.Id.ToString());
            tags.RootElement.GetProperty("role").GetString().Should().Be("developer");
        }
    }

    [Test]
    public async Task Resolve_DisabledSelection_NeverMaterialisesDisabledPersona()
    {
        var enablement = new FakeEnablement();
        // A persona-prompt resolver that records which roles it was asked for —
        // proving the disabled persona is never materialised.
        var persona = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement,
            defaultPersonaName: "claude", personaPrompts: persona);
        await using (h.Ctx)
        {
            var def = await SeedPersonaAsync(h.Agents, "claude");
            var picked = await SeedPersonaAsync(h.Agents, "gemini");
            enablement.Enable(def.Id);
            enablement.Enable(picked.Id);
            await h.Registry.SelectForRoleAsync("developer", picked.Id, null);
            enablement.Disable(picked.Id);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            // The resolved handle is the enabled default, never the disabled pick.
            resolved.Handle.Should().Be("claude");
            resolved.AgentId.Should().NotBe(picked.Id);
        }
    }

    // ── (4) Enabled-default lookup ──

    [Test]
    public async Task GetSystemDefault_ConfiguredDefaultEnabled_Returned()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            var claude = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(claude.Id);

            var result = await h.Registry.GetSystemDefaultPublicAsync("developer");

            result.Should().NotBeNull();
            result!.Id.Should().Be(claude.Id);
        }
    }

    [Test]
    public async Task GetSystemDefault_ConfiguredDisabled_FallsToEnabledDefault()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            await SeedPersonaAsync(h.Agents, "claude");     // configured but NOT enabled
            var gemini = await SeedPersonaAsync(h.Agents, "gemini");
            enablement.Enable(gemini.Id);
            enablement.EnabledDefault = gemini.Id;          // 32-16 says gemini is the enabled default

            var result = await h.Registry.GetSystemDefaultPublicAsync("developer");

            result.Should().NotBeNull();
            result!.Id.Should().Be(gemini.Id);
        }
    }

    [Test]
    public async Task GetSystemDefault_NothingEnabled_ReturnsNull()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            await SeedPersonaAsync(h.Agents, "claude"); // seeded but NOT enabled

            var result = await h.Registry.GetSystemDefaultPublicAsync("developer");

            result.Should().BeNull("nothing enabled ⇒ no default; the resolver fails loud");
        }
    }

    [Test]
    public async Task Resolve_NothingEnabled_FailsLoud_NoEnabledDefault()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            await SeedPersonaAsync(h.Agents, "claude"); // seeded, NOT enabled, no own-private

            Func<Task> act = async () => await h.Resolver.ResolveForRoleAsync("developer");

            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT.RESOLVE.NO_ENABLED_DEFAULT");

            h.Events.Captured.Should().ContainSingle(e => e.Type == "AGENT.RESOLVE.FAILED");
        }
    }

    // ── (5/7) Persona prompt dispatched to IPersonaPromptResolver (boundary) ──

    [Test]
    public async Task Resolve_EnabledPersona_PromptDispatchedToPersonaSeam_NotPromptStore()
    {
        var enablement = new FakeEnablement();
        var persona = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var custom = new CountingCustomPrompts("[SHOULD NOT BE USED]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement,
            defaultPersonaName: "claude", personaPrompts: persona, customPrompts: custom);
        await using (h.Ctx)
        {
            var claude = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(claude.Id);

            var resolved = await h.Resolver.ResolveForRoleAsync("architect");

            resolved.SystemPrompt.Should().Be("[SEAM PROMPT]");
            resolved.PromptSource.Should().Be(AgentPromptSource.Epic27Store);
            persona.CallCount.Should().Be(1, "the public persona dispatches to IPersonaPromptResolver");
            custom.CallCount.Should().Be(0, "a persona NEVER takes the custom-agent seam");
        }
    }

    [Test]
    public async Task Resolve_EnabledPersona_SeamFailsLoud_Propagates_NoEmptyFallback()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement,
            defaultPersonaName: "claude", personaPrompts: new ThrowingPersonaPrompts());
        await using (h.Ctx)
        {
            var claude = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(claude.Id);

            Func<Task> act = async () => await h.Resolver.ResolveForRoleAsync("architect");
            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("PROMPT_UNRESOLVED");
        }
    }

    // ── (6) Action key plumbing ──

    [Test]
    public async Task Resolve_WithAction_ThreadsActionToPersonaSeam()
    {
        var enablement = new FakeEnablement();
        var persona = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement,
            defaultPersonaName: "claude", personaPrompts: persona);
        await using (h.Ctx)
        {
            var claude = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(claude.Id);

            await h.Resolver.ResolveForRoleAsync("developer", action: "implement-feature");

            persona.LastRole.Should().Be("developer");
            persona.LastAction.Should().Be("implement-feature",
                "the action key is threaded to the persona prompt source (AC7)");
        }
    }

    [Test]
    public async Task Resolve_WithoutAction_PassesNullActionToPersonaSeam()
    {
        var enablement = new FakeEnablement();
        var persona = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement,
            defaultPersonaName: "claude", personaPrompts: persona);
        await using (h.Ctx)
        {
            var claude = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(claude.Id);

            await h.Resolver.ResolveForRoleAsync("developer");

            persona.LastAction.Should().BeNull("absent action ⇒ role-system / action-default branch");
        }
    }

    // ── (8) Mode-parameterized principal ──

    [Test]
    [TestCase(TammaMode.SaaS)]
    [TestCase(TammaMode.SingleUser)]
    public async Task Resolve_Gate_EvaluatedAgainstModePrincipal(TammaMode mode)
    {
        var enablement = new FakeEnablement();
        var tenantId = mode == TammaMode.SaaS ? (Guid?)TenantA : null;
        var userId = mode == TammaMode.SingleUser ? (Guid?)UserA : null;
        var h = BuildHarness(mode, tenantId, userId, enablement, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            var claude = await SeedPersonaAsync(h.Agents, "claude");
            enablement.Enable(claude.Id);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.Handle.Should().Be("claude");
            // The gate consulted the principal matching the mode.
            var lastPrincipal = enablement.LastPrincipal;
            if (mode == TammaMode.SaaS)
            {
                lastPrincipal.TenantId.Should().Be(TenantA);
                lastPrincipal.UserId.Should().BeNull();
            }
            else
            {
                lastPrincipal.UserId.Should().Be(UserA);
                lastPrincipal.TenantId.Should().BeNull();
            }
        }
    }

    // ── (9) No credential in the resolve path ──

    [Test]
    public async Task Resolve_NeverCarriesACredential_ProviderAndModelOnly()
    {
        var enablement = new FakeEnablement();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, enablement, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            var claude = await h.Agents.CreateAsync(
                new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public },
                """{ "provider": "anthropic", "model": "claude-sonnet-4-20250514" }""", "seed", null);
            enablement.Enable(claude.Id);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.Provider.Should().Be("anthropic");
            resolved.Model.Should().Be("claude-sonnet-4-20250514");
            // ResolvedAgentConfig has NO ApiKey/credential field — assert by
            // reflection that nothing key-shaped exists on the resolved config.
            var props = typeof(ResolvedAgentConfig).GetProperties().Select(p => p.Name).ToList();
            props.Should().NotContain(p => p.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
            props.Should().NotContain(p => p.Contains("Credential", StringComparison.OrdinalIgnoreCase));
            props.Should().NotContain(p => p.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── test doubles ──

    /// <summary>Story 32-16 read-seam fake. Records calls so tests can assert the
    /// gate was evaluated against the right principal and never for own-private.</summary>
    private sealed class FakeEnablement : ITenantAgentEnablementReader
    {
        private readonly HashSet<Guid> _enabled = new();
        public List<Guid> IsEnabledCalls { get; } = new();
        public Principal LastPrincipal { get; private set; }
        public Guid? EnabledDefault { get; set; }

        public void Enable(Guid id) => _enabled.Add(id);
        public void Disable(Guid id) => _enabled.Remove(id);

        public Task<bool> IsEnabledForPrincipalAsync(Guid agentId, Principal principal, CancellationToken ct = default)
        {
            IsEnabledCalls.Add(agentId);
            LastPrincipal = principal;
            return Task.FromResult(_enabled.Contains(agentId));
        }

        public Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(Principal principal, CancellationToken ct = default)
        {
            LastPrincipal = principal;
            return Task.FromResult((IReadOnlyList<Guid>)_enabled.ToList());
        }

        public Task<Guid?> GetEnabledDefaultPersonaIdAsync(Principal principal, CancellationToken ct = default)
        {
            LastPrincipal = principal;
            // If a test set an explicit enabled default, honour it; else the single
            // enabled persona if unambiguous, else null (mirrors the real service).
            if (EnabledDefault is { } d && _enabled.Contains(d)) return Task.FromResult<Guid?>(d);
            return Task.FromResult(_enabled.Count == 1 ? _enabled.Single() : (Guid?)null);
        }
    }

    private sealed class CapturingPersonaPrompts(string prompt) : IPersonaPromptResolver
    {
        public int CallCount { get; private set; }
        public string? LastRole { get; private set; }
        public string? LastAction { get; private set; }
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
        {
            CallCount++;
            LastRole = role;
            LastAction = action;
            return Task.FromResult(prompt);
        }
    }

    private sealed class ThrowingPersonaPrompts : IPersonaPromptResolver
    {
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
            => throw new TammaError("PROMPT_UNRESOLVED", "no prompt", retryable: false,
                severity: TammaErrorSeverity.High);
    }

    private sealed class CountingCustomPrompts(string prompt) : ICustomAgentPromptResolver
    {
        public int CallCount { get; private set; }
        public Task<string> ResolveAsync(
            Guid agentId, AgentPromptSet prompts, string role, string? action, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(prompt);
        }
    }

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

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
