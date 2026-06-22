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
    private Harness BuildHarness(
        TammaMode mode, Guid? tenantId, Guid? userId, string defaultPersonaName = "tamma-developer",
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

        // Story 32-15 — the system default is now the configured DEFAULT PERSONA
        // (by name, role-independent). These chain tests seed tamma-<role> public
        // rows, so point the default persona at the seeded handle the test uses.
        var personaOptions = Microsoft.Extensions.Options.Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = defaultPersonaName });

        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, modeProvider, tenantContext, httpAccessor,
            personaOptions, NullLogger<AgentRegistryService>.Instance);

        // The legacy JSONB repo is never exercised by the entity-aware chain; a
        // real instance is wired so the full constructor is satisfied. Story
        // 32-15 — a stub persona prompt resolver supplies the PUBLIC branch's
        // system prompt (persona = prompt-free; prompt comes from the seam).
        var legacyRepo = new AgentConfigRepository(factory);
        // Story 32-17 — the custom/private prompt seam. Default to the REAL
        // CustomAgentPromptResolver, which resolves from the prompt set the
        // resolver threads in from the already-loaded version (no repo re-read).
        var resolver = new AgentResolverService(
            legacyRepo, null, NullLogger<AgentResolverService>.Instance,
            registry, agentRepo, events, null, personaPrompts ?? new StubPersonaPrompts(),
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
        // Story 32-15 — the default is the configured persona (by name). Point it
        // at the seeded handle so the role-independent default resolves.
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, defaultPersonaName: "tamma-architect");
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
            // 32-2 review #2: SELECTING a public agent stores/emits the provenance
            // the resolver stamps for it — "tenant-public" — not "system-public"
            // (which is reserved for the unselected system-default fallback).
            tags.RootElement.GetProperty("source").GetString().Should().Be("tenant-public");
        }
    }

    // ── Story 32-15 — persona prompt sourced from the IPersonaPromptResolver seam ──

    [Test]
    public async Task Resolve_PublicPersona_PromptComesFromSeam_NotFromConfig()
    {
        // The persona's ConfigJson carries NO prompt; MaterialiseAsync's public
        // branch sources the system prompt from the IPersonaPromptResolver seam.
        var capturing = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null,
            defaultPersonaName: "claude", personaPrompts: capturing);
        await using (h.Ctx)
        {
            // Persona-style public agent: Role=NULL, prompt-free config.
            var persona = await h.Agents.CreateAsync(
                new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public },
                """{ "provider": "anthropic", "model": "claude-sonnet-4-20250514" }""", "seed", null);

            var resolved = await h.Resolver.ResolveForRoleAsync("architect");

            resolved.AgentId.Should().Be(persona.Id);
            resolved.Source.Should().Be("system-public");
            resolved.SystemPrompt.Should().Be("[SEAM PROMPT]",
                "the persona prompt comes from the Epic 27 seam, not the prompt-free config");
            capturing.LastRole.Should().Be("architect");
            capturing.CallCount.Should().Be(1, "the public branch invokes the seam exactly once");
        }
    }

    [Test]
    public async Task Resolve_PublicPersona_SeamFailsLoud_Propagates()
    {
        // Epic 27 returns nothing for (role, action) → the seam throws
        // PROMPT_UNRESOLVED; MaterialiseAsync must NOT swallow it into an
        // empty/plain prompt.
        var failing = new ThrowingPersonaPrompts();
        var h = BuildHarness(TammaMode.SaaS, TenantA, null,
            defaultPersonaName: "claude", personaPrompts: failing);
        await using (h.Ctx)
        {
            await h.Agents.CreateAsync(
                new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public },
                """{ "provider": "anthropic", "model": "claude-sonnet-4-20250514" }""", "seed", null);

            Func<Task> act = async () => await h.Resolver.ResolveForRoleAsync("architect");
            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("PROMPT_UNRESOLVED");
        }
    }

    [Test]
    public async Task Resolve_StampsAgentIdAndVersion_AndMergesDefault()
    {
        // Merge + stamp are preserved (Story 32-2 behaviour kept through 32-15).
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, defaultPersonaName: "claude");
        await using (h.Ctx)
        {
            var persona = await h.Agents.CreateAsync(
                new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public },
                """{ "provider": "openai", "model": "gpt-4o" }""", "seed", null);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.AgentId.Should().Be(persona.Id);
            resolved.AgentVersion.Should().Be(1);
            resolved.Provider.Should().Be("openai", "the persona config overrides the role default");
            resolved.Model.Should().Be("gpt-4o");
        }
    }

    [Test]
    public async Task EndToEnd_SeedPersonas_ResolveConfiguredDefault()
    {
        // AC15 — seed the named personas (with the price book), set the default
        // to "gemini", and resolve any role → the gemini persona with its
        // explicit provider+model, prompt from the Epic 27 seam.
        await using (var seedCtx = NewContext())
        {
            await seedCtx.Database.ExecuteSqlRawAsync(
                "TRUNCATE provider_model_prices, providers CASCADE;");
            await Tamma.Data.Seeders.ProviderPricingSeeder.SeedAsync(seedCtx);
            await Tamma.Data.Seeders.AgentEntitySeeder.SeedAsync(seedCtx);
        }

        var h = BuildHarness(TammaMode.SaaS, TenantA, null, defaultPersonaName: "gemini");
        await using (h.Ctx)
        {
            var resolved = await h.Resolver.ResolveForRoleAsync("architect");

            resolved.Source.Should().Be("system-public");
            resolved.Handle.Should().Be("gemini");
            resolved.Provider.Should().Be("google");
            resolved.Model.Should().Be("gemini-1.5-pro");
            resolved.SystemPrompt.Should().NotBeNullOrWhiteSpace("prompt comes from the Epic 27 seam");
        }

        // Clean up the price rows we seeded so the shared fixture stays tidy.
        await using var cleanup = NewContext();
        await cleanup.Database.ExecuteSqlRawAsync(
            "TRUNCATE provider_model_prices, providers CASCADE;");
    }

    // ── Story 32-17 — custom/private prompt-source branch ──

    private const string CustomCfg = """
        {
          "provider": "anthropic",
          "model": "claude-sonnet-4",
          "prompts": {
            "system": "ATLAS SYSTEM PROMPT",
            "byRoleAction": { "developer:implement-feature": "ATLAS IMPLEMENT PROMPT" }
          }
        }
        """;

    [Test]
    public async Task Resolve_CustomPrivateAgent_PromptFromOwnPrompts_PersonaSeamNotConsulted()
    {
        var persona = new CountingPersonaPrompts("[SHOULD NOT BE USED]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, personaPrompts: persona);
        await using (h.Ctx)
        {
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                CustomCfg, "seed", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            // (phase, role) → action non-null so byRoleAction can match.
            var resolved = await h.Resolver.ResolveForRoleAndPhaseAsync("implement-feature", "developer");

            resolved.AgentId.Should().Be(priv.Id);
            resolved.Source.Should().Be("tenant-private");
            resolved.SystemPrompt.Should().Be("ATLAS IMPLEMENT PROMPT",
                "byRoleAction wins over system on the custom branch");
            resolved.PromptSource.Should().Be(AgentPromptSource.CustomAgent);
            persona.CallCount.Should().Be(0, "the custom branch NEVER consults the Epic 27 persona seam");
        }
    }

    [Test]
    public async Task Resolve_CustomPrivateAgent_RoleOnly_FallsToSystem()
    {
        var persona = new CountingPersonaPrompts("[SHOULD NOT BE USED]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, personaPrompts: persona);
        await using (h.Ctx)
        {
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                CustomCfg, "seed", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            // Role-only resolution (action null) → no byRoleAction key → system.
            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.SystemPrompt.Should().Be("ATLAS SYSTEM PROMPT");
            resolved.PromptSource.Should().Be(AgentPromptSource.CustomAgent);
            persona.CallCount.Should().Be(0);
        }
    }

    [Test]
    public async Task Resolve_CustomPrivateAgent_NoMatch_FailsLoud_PersonaSeamNotConsulted()
    {
        var persona = new CountingPersonaPrompts("[SHOULD NOT BE USED]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, personaPrompts: persona);
        await using (h.Ctx)
        {
            // Prompts block carries ONLY a byRoleAction for developer:implement-feature
            // and no system → a request for a different action fails loud.
            const string onlyRoleAction = """
                {
                  "provider": "anthropic", "model": "claude-sonnet-4",
                  "prompts": { "byRoleAction": { "developer:implement-feature": "ONLY THIS" } }
                }
                """;
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                onlyRoleAction, "seed", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            Func<Task> act = async () =>
                await h.Resolver.ResolveForRoleAndPhaseAsync("write-tests", "developer");

            // I1 — the custom leg fails loud with TammaError (symmetric with the
            // persona leg's PROMPT_UNRESOLVED), not a bespoke exception type.
            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("CUSTOM_PROMPT_UNRESOLVED");
            persona.CallCount.Should().Be(0,
                "a custom-branch no-resolve NEVER falls through to the Epic 27 persona seam");
        }
    }

    [Test]
    public async Task Resolve_PrivateAgent_EmptyPromptsBlock_DelegatesToPersonaBranch()
    {
        var persona = new CountingPersonaPrompts("[PERSONA PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, personaPrompts: persona);
        await using (h.Ctx)
        {
            // Private agent with an EMPTY prompts block → custom branch NOT
            // entered → delegates to the 32-15 persona/Epic-27 branch.
            const string emptyPrompts = """
                { "provider": "anthropic", "model": "claude-sonnet-4", "prompts": {} }
                """;
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                emptyPrompts, "seed", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.SystemPrompt.Should().Be("[PERSONA PROMPT]");
            resolved.PromptSource.Should().Be(AgentPromptSource.Epic27Store);
            persona.CallCount.Should().Be(1, "an empty prompts block delegates to the persona seam");
        }
    }

    [Test]
    public async Task Resolve_PrivateAgent_NoPromptsKey_DelegatesToPersonaBranch()
    {
        var persona = new CountingPersonaPrompts("[PERSONA PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null, personaPrompts: persona);
        await using (h.Ctx)
        {
            // Private agent with NO prompts key at all → persona branch.
            var priv = await SeedTenantPrivateAsync(h.Agents, TenantA, "developer", "atlas");
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            var resolved = await h.Resolver.ResolveForRoleAsync("developer");

            resolved.PromptSource.Should().Be(AgentPromptSource.Epic27Store);
            persona.CallCount.Should().Be(1);
        }
    }

    [Test]
    public async Task Resolve_CustomPrivateAgent_NoTemplateBody_InEmittedEvents()
    {
        var h = BuildHarness(TammaMode.SaaS, TenantA, null);
        await using (h.Ctx)
        {
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                CustomCfg, "seed", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);
            h.Events.Captured.Clear();

            var resolved = await h.Resolver.ResolveForRoleAndPhaseAsync("implement-feature", "developer");
            resolved.SystemPrompt.Should().Be("ATLAS IMPLEMENT PROMPT");

            // AC7 — no resolution event from the resolver carries a template body
            // in its Tags or Data (the resolver doesn't emit on success; assert it
            // stays that way and that nothing leaked the body).
            foreach (var evt in h.Events.Captured)
            {
                (evt.Tags ?? "").Should().NotContain("ATLAS IMPLEMENT PROMPT");
                (evt.Data ?? "").Should().NotContain("ATLAS IMPLEMENT PROMPT");
                (evt.Tags ?? "").Should().NotContain("ATLAS SYSTEM PROMPT");
                (evt.Data ?? "").Should().NotContain("ATLAS SYSTEM PROMPT");
            }
        }
    }

    // ── C2 — the loaded prompt set is THREADED into the custom seam (no re-read) ──

    [Test]
    public async Task Resolve_CustomBranch_ThreadsLoadedPromptSet_IntoSeam_NoReRead()
    {
        // A capturing custom resolver records the AgentPromptSet it is handed. The
        // resolver itself does NO repository read (its only collaborator is a
        // logger) — MaterialiseAsync parses the set ONCE from the loaded version
        // and threads it in. Proving the seam receives the loaded version's
        // prompts (not a fresh re-fetch) closes the stale/torn-read window.
        var capturing = new CapturingCustomPrompts("CAPTURED");
        var persona = new CountingPersonaPrompts("[SHOULD NOT BE USED]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null,
            personaPrompts: persona, customPrompts: capturing);
        await using (h.Ctx)
        {
            var priv = await h.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                CustomCfg, "seed", null);
            await h.Registry.SelectForRoleAsync("developer", priv.Id, null);

            var resolved = await h.Resolver.ResolveForRoleAndPhaseAsync("implement-feature", "developer");

            resolved.SystemPrompt.Should().Be("CAPTURED");
            resolved.PromptSource.Should().Be(AgentPromptSource.CustomAgent);
            capturing.CallCount.Should().Be(1);
            capturing.LastAgentId.Should().Be(priv.Id);
            // The threaded set is the SAME prompts parsed from the loaded version —
            // it carries the active version's byRoleAction cell, not a re-fetch.
            capturing.LastPrompts.Should().NotBeNull();
            capturing.LastPrompts!.ByRoleAction.Should().ContainKey("developer:implement-feature");
            capturing.LastPrompts!.ByRoleAction!["developer:implement-feature"]
                .Should().Be("ATLAS IMPLEMENT PROMPT");
            persona.CallCount.Should().Be(0);
        }
    }

    [Test]
    public async Task Resolve_PublicPersona_PromptSourceTag_IsEpic27Store()
    {
        var persona = new CapturingPersonaPrompts("[SEAM PROMPT]");
        var h = BuildHarness(TammaMode.SaaS, TenantA, null,
            defaultPersonaName: "claude", personaPrompts: persona);
        await using (h.Ctx)
        {
            await h.Agents.CreateAsync(
                new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public },
                """{ "provider": "anthropic", "model": "claude-sonnet-4-20250514" }""", "seed", null);

            var resolved = await h.Resolver.ResolveForRoleAsync("architect");

            resolved.PromptSource.Should().Be(AgentPromptSource.Epic27Store);
        }
    }

    /// <summary>C2 — captures the AgentPromptSet the resolver threads into the
    /// custom seam (proving the loaded version's prompts are passed, not re-read).
    /// The real <see cref="CustomAgentPromptResolver"/> has no repo at all; this
    /// double records what it was handed and returns a deterministic prompt.</summary>
    private sealed class CapturingCustomPrompts(string prompt) : ICustomAgentPromptResolver
    {
        public int CallCount { get; private set; }
        public Guid LastAgentId { get; private set; }
        public AgentPromptSet? LastPrompts { get; private set; }
        public Task<string> ResolveAsync(
            Guid agentId, AgentPromptSet prompts, string role, string? action, CancellationToken ct = default)
        {
            CallCount++;
            LastAgentId = agentId;
            LastPrompts = prompts;
            return Task.FromResult(prompt);
        }
    }

    /// <summary>Counts invocations so a test can assert the persona seam is (not)
    /// consulted, while still supplying a deterministic prompt when it IS used.</summary>
    private sealed class CountingPersonaPrompts(string prompt) : IPersonaPromptResolver
    {
        public int CallCount { get; private set; }
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(prompt);
        }
    }

    // ── test doubles ──

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private sealed class CapturingPersonaPrompts(string prompt) : IPersonaPromptResolver
    {
        public int CallCount { get; private set; }
        public string? LastRole { get; private set; }
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
        {
            CallCount++;
            LastRole = role;
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

    /// <summary>Story 32-15 — supplies the PUBLIC branch's system prompt (a
    /// persona is prompt-free; its prompt comes from this seam). Returns a
    /// non-empty deterministic prompt so the chain tests' public resolution
    /// succeeds without standing up the full Epic 27 store.</summary>
    private sealed class StubPersonaPrompts : IPersonaPromptResolver
    {
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
            => Task.FromResult($"[persona system prompt for role={role}]");
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
