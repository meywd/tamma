using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Endpoints;
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
/// Story 32-2 — endpoint-level tests for the /api/agents resolution + selection
/// surface. Drives the <see cref="AgentEndpoints"/> handlers directly (the same
/// delegates Program.cs maps) against a real Postgres testcontainer, with the
/// real registry/resolver/selection stack. Covers: resolve → enriched config,
/// bad role → 400, unresolvable → 404 (no blank), select round-trip, cross-tenant
/// select → 404, rollback endpoint, and the public-write 403 gate.
/// </summary>
[TestFixture]
public class AgentRegistryEndpointsTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-32e2-32e2-32e2-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-32e2-32e2-32e2-bbbbbbbbbbbb");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_registry_ep_test")
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

    private Stack BuildStack(
        TammaMode mode, Guid? tenantId, Guid? userId, bool platformAdmin = false,
        string defaultPersonaName = "tamma-developer")
    {
        var cpCtx = NewContext();
        var events = new CapturingEvents();
        var agentRepo = new AgentRepository(cpCtx, events);

        var tenantContext = new TenantContext();
        if (tenantId is Guid tid) tenantContext.SetTenantId(tid);

        var factory = new SameDbTenantFactory(_connectionString);
        var selectionRepo = new AgentSelectionRepository(cpCtx, factory, tenantContext);
        var modeProvider = new StubMode(mode);
        var principal = Principal(userId, platformAdmin);
        var httpAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };

        // Story 32-15 — the system default is the configured default PERSONA (by
        // name, role-independent). Point it at the handle the test seeds.
        var personaOptions = Microsoft.Extensions.Options.Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = defaultPersonaName });

        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, modeProvider, tenantContext, httpAccessor,
            personaOptions, NullLogger<AgentRegistryService>.Instance);
        var resolver = new AgentResolverService(
            new AgentConfigRepository(factory), null, NullLogger<AgentResolverService>.Instance,
            registry, agentRepo, events, null, new StubPersonaPrompts());

        return new Stack(
            agentRepo, registry, resolver, principal, tenantContext, modeProvider, cpCtx);
    }

    private sealed record Stack(
        AgentRepository Agents,
        AgentRegistryService Registry,
        AgentResolverService Resolver,
        ClaimsPrincipal Principal,
        TenantContext TenantContext,
        ITammaModeProvider Mode,
        ControlPlaneDbContext Ctx);

    private static ClaimsPrincipal Principal(Guid? userId, bool platformAdmin = false)
    {
        var claims = new List<Claim>();
        if (userId is Guid uid) claims.Add(new Claim(ClaimTypes.NameIdentifier, uid.ToString()));
        if (platformAdmin) claims.Add(new Claim("platformRole", "platform_admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        if (ctx.Response.Body.Length == 0)
        {
            return (ctx.Response.StatusCode, default);
        }
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    private async Task<Agent> SeedPublicAsync(AgentRepository repo, string role, string handle)
        => await repo.CreateAsync(
            new Agent { Name = handle, Role = role, Visibility = AgentVisibility.Public }, Cfg, null, null);

    /// <summary>Story 32-15 — seed a named cross-role PUBLIC persona (Role=NULL).</summary>
    private async Task<Agent> SeedPersonaAsync(AgentRepository repo, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

    private async Task<Agent> SeedTenantPrivateAsync(AgentRepository repo, Guid tenantId, string role, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = role, Visibility = AgentVisibility.Private, OwnerTenantId = tenantId },
            Cfg, null, null);

    // ── Resolve endpoint ──

    [Test]
    public async Task Resolve_NoRole_Returns400()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var (status, _) = await ExecuteAsync(await AgentEndpoints.Resolve(null, null, s.Resolver));
            status.Should().Be(StatusCodes.Status400BadRequest);
        }
    }

    [Test]
    public async Task Resolve_BadRole_Returns400()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var (status, _) = await ExecuteAsync(await AgentEndpoints.Resolve("wizard", null, s.Resolver));
            status.Should().Be(StatusCodes.Status400BadRequest);
        }
    }

    [Test]
    public async Task Resolve_Unresolvable_Returns404_NoBlankConfig()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            // No agent seeded — fail-loud path → 404 with code, no config body.
            var (status, body) = await ExecuteAsync(await AgentEndpoints.Resolve("tester", null, s.Resolver));
            status.Should().Be(StatusCodes.Status404NotFound);
            body.GetProperty("error").GetString().Should().Be("agent_resolve_no_default");
        }
    }

    [Test]
    public async Task Resolve_SystemDefault_ReturnsEnrichedConfig()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var pub = await SeedPublicAsync(s.Agents, "developer", "tamma-developer");
            var (status, body) = await ExecuteAsync(await AgentEndpoints.Resolve("developer", null, s.Resolver));
            status.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("source").GetString().Should().Be("system-public");
            body.GetProperty("agentId").GetGuid().Should().Be(pub.Id);
            body.GetProperty("agentVersion").GetInt32().Should().Be(1);
        }
    }

    // ── Select round-trip + respected by resolve ──

    [Test]
    public async Task SelectForRole_Persists_And_RespectedByResolve()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            await SeedPublicAsync(s.Agents, "developer", "tamma-developer");
            var priv = await SeedTenantPrivateAsync(s.Agents, TenantA, "developer", "atlas");

            var sel = await AgentEndpoints.SelectForRole(
                "developer", new SelectRoleRequest(priv.Id), s.Registry, s.Principal);
            var (selStatus, _) = await ExecuteAsync(sel);
            selStatus.Should().Be(StatusCodes.Status200OK);

            var (resStatus, body) = await ExecuteAsync(
                await AgentEndpoints.Resolve("developer", null, s.Resolver));
            resStatus.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("agentId").GetGuid().Should().Be(priv.Id);
            body.GetProperty("source").GetString().Should().Be("tenant-private");
        }
    }

    [Test]
    public async Task SelectForRole_PublicAgent_StoredHintMatchesResolvedSource_TenantPublic()
    {
        // Review follow-up (32-2 #2): a principal SELECTING a public agent must
        // store the SAME provenance the resolver stamps for it — "tenant-public"
        // — so GET /role-selections and Resolve agree (not "system-public").
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var pub = await SeedPublicAsync(s.Agents, "developer", "tamma-developer");

            var sel = await AgentEndpoints.SelectForRole(
                "developer", new SelectRoleRequest(pub.Id), s.Registry, s.Principal);
            var (selStatus, selBody) = await ExecuteAsync(sel);
            selStatus.Should().Be(StatusCodes.Status200OK);
            // PUT response carries the stored hint.
            selBody.GetProperty("visibility").GetString().Should().Be("tenant-public");

            // GET /role-selections reports the stored hint.
            var (listStatus, listBody) = await ExecuteAsync(
                await AgentEndpoints.GetRoleSelections(s.Registry));
            listStatus.Should().Be(StatusCodes.Status200OK);
            var stored = listBody.EnumerateArray()
                .Single(e => e.GetProperty("role").GetString() == "developer")
                .GetProperty("visibility").GetString();

            // Resolve reports the live source.
            var (resStatus, resBody) = await ExecuteAsync(
                await AgentEndpoints.Resolve("developer", null, s.Resolver));
            resStatus.Should().Be(StatusCodes.Status200OK);
            var resolvedSource = resBody.GetProperty("source").GetString();

            resolvedSource.Should().Be("tenant-public");
            stored.Should().Be(resolvedSource,
                "the stored selection hint must match the resolved source for the same selection");
        }
    }

    [Test]
    public async Task SelectForRole_LoserOfFirstTimeRace_ReReadsAndUpdates_NoConflict_LastWriterWins()
    {
        // Review follow-up (32-2 #1), DETERMINISTIC half: the loser path is forced
        // by hand. repoB reads null (no row yet), THEN a competing writer commits
        // the winner row, THEN repoB saves → unique (TenantId,UserId,Role)
        // violation. The repo must catch it, re-read the winner, and UPDATE it
        // (no raw DbUpdateException → no 500). Final value = repoB's later write.
        Guid agentWinner, agentLoserWrite;
        await using (var seed = NewContext())
        {
            var repo = new AgentRepository(seed, new CapturingEvents());
            agentWinner = (await repo.CreateAsync(
                new Agent { Name = "atlas-w", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                Cfg, null, null)).Id;
            agentLoserWrite = (await repo.CreateAsync(
                new Agent { Name = "atlas-l", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                Cfg, null, null)).Id;
        }

        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(TenantA);

        // A factory that, on its FIRST CreateAsync, hands back a context whose
        // SaveChanges is guaranteed to lose: we pre-commit the winner row through
        // a SEPARATE connection AFTER the repo's read but BEFORE its save, by
        // wrapping the read so the insert lands in between.
        var factory = new BarrierTenantFactory(_connectionString, async () =>
        {
            // Competing writer commits the winner row while repoB sits between its
            // read (already returned null) and its save.
            await using var competitor = NewContext();
            competitor.AgentRoleSelections.Add(new AgentRoleSelection
            {
                Id = Guid.NewGuid(), TenantId = TenantA, UserId = null, Role = "developer",
                AgentId = agentWinner, Visibility = "tenant-private",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await competitor.SaveChangesAsync();
        });

        await using var ctxB = NewContext();
        var repoB = new AgentSelectionRepository(ctxB, factory, tenantContext);

        var (entity, wasCreated) = await repoB.UpsertByTenantAsync(
            TenantA, "developer", agentLoserWrite, "tenant-private", null);

        wasCreated.Should().BeFalse("the loser re-reads and updates the winner's existing row");
        entity.AgentId.Should().Be(agentLoserWrite, "last-writer-wins: repoB's value survives");

        await using var verify = NewContext();
        var rows = await verify.AgentRoleSelections.IgnoreQueryFilters()
            .Where(s => s.TenantId == TenantA && s.Role == "developer").ToListAsync();
        rows.Should().ContainSingle("exactly one selection per (tenant, role) survives the race");
        rows.Single().AgentId.Should().Be(agentLoserWrite);
    }

    [Test]
    public async Task SelectForRole_ConcurrentFirstTimeSelects_SameRole_OneRow_NoConflict()
    {
        // Review follow-up (32-2 #1), STRESS half: two genuinely concurrent
        // first-time selects for the same (principal, role). Whichever loses the
        // INSERT race re-reads→updates rather than throwing. Looped to make the
        // race reliably surface; the invariant (one row, no throw) holds every run.
        Guid agentA, agentB;
        await using (var seed = NewContext())
        {
            var repo = new AgentRepository(seed, new CapturingEvents());
            agentA = (await repo.CreateAsync(
                new Agent { Name = "atlas-a", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                Cfg, null, null)).Id;
            agentB = (await repo.CreateAsync(
                new Agent { Name = "atlas-b", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                Cfg, null, null)).Id;
        }

        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(TenantA);
        var factory = new SameDbTenantFactory(_connectionString);

        for (var i = 0; i < 5; i++)
        {
            await using (var reset = NewContext())
            {
                await reset.Database.ExecuteSqlRawAsync("TRUNCATE agent_role_selections CASCADE;");
            }

            await using var ctxA = NewContext();
            await using var ctxB = NewContext();
            var repoA = new AgentSelectionRepository(ctxA, factory, tenantContext);
            var repoB = new AgentSelectionRepository(ctxB, factory, tenantContext);

            var taskA = repoA.UpsertByTenantAsync(TenantA, "developer", agentA, "tenant-private", null);
            var taskB = repoB.UpsertByTenantAsync(TenantA, "developer", agentB, "tenant-private", null);

            Func<Task> act = async () => await Task.WhenAll(taskA, taskB);
            await act.Should().NotThrowAsync(
                "the upsert must absorb the unique-violation race rather than surfacing a 500");

            await using var verify = NewContext();
            var rows = await verify.AgentRoleSelections.IgnoreQueryFilters()
                .Where(s => s.TenantId == TenantA && s.Role == "developer").ToListAsync();
            rows.Should().ContainSingle("exactly one selection per (tenant, role) survives the race");
            new[] { agentA, agentB }.Should().Contain(rows.Single().AgentId,
                "the surviving row holds one of the two concurrently-written agents");
        }
    }

    [Test]
    public async Task SelectForRole_BadRole_Returns400()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var sel = await AgentEndpoints.SelectForRole(
                "wizard", new SelectRoleRequest(Guid.NewGuid()), s.Registry, s.Principal);
            var (status, _) = await ExecuteAsync(sel);
            status.Should().Be(StatusCodes.Status400BadRequest);
        }
    }

    [Test]
    public async Task SelectForRole_CrossTenantPrivateTarget_Returns404()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            // Agent owned by TenantB; TenantA's principal must get 404, not 403.
            var foreign = await SeedTenantPrivateAsync(s.Agents, TenantB, "developer", "their-atlas");
            var sel = await AgentEndpoints.SelectForRole(
                "developer", new SelectRoleRequest(foreign.Id), s.Registry, s.Principal);
            var (status, body) = await ExecuteAsync(sel);
            status.Should().Be(StatusCodes.Status404NotFound);
            body.GetProperty("error").GetString().Should().Be("agent_not_found");
        }
    }

    [Test]
    public async Task GetSystemDefaultPublic_ReturnsConfiguredPersona_RoleIndependent_NoAmbiguityWarning()
    {
        // Story 32-15 — public agents are cross-role PERSONAS, so the system
        // default is the configured default persona (by name), resolved REGARDLESS
        // of role. The old per-role ">1 public agent for this role" ambiguity
        // warning is DELETED — seeding several public personas must NOT log it.
        var logProvider = new Infrastructure.CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logProvider));

        var cpCtx = NewContext();
        var events = new CapturingEvents();
        var agentRepo = new AgentRepository(cpCtx, events);
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(TenantA);
        var factory = new SameDbTenantFactory(_connectionString);
        var selectionRepo = new AgentSelectionRepository(cpCtx, factory, tenantContext);
        var personaOptions = Microsoft.Extensions.Options.Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = "claude" });
        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, new StubMode(TammaMode.SaaS),
            tenantContext, new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            personaOptions, loggerFactory.CreateLogger<AgentRegistryService>());

        await using (cpCtx)
        {
            // Several public personas (Role=NULL, cross-role).
            var claude = await SeedPersonaAsync(agentRepo, "claude");
            await SeedPersonaAsync(agentRepo, "gemini");
            await SeedPersonaAsync(agentRepo, "codegpt");

            // Resolves the configured persona for ANY role — never role-matches.
            foreach (var role in new[] { "developer", "architect", "tester" })
            {
                var chosen = await registry.GetSystemDefaultPublicAsync(role);
                chosen.Should().NotBeNull();
                chosen!.Id.Should().Be(claude.Id,
                    "the configured default persona is returned regardless of role");
            }

            logProvider.Entries.Should().NotContain(e =>
                e.Message.Contains("agent.system_default.ambiguous"),
                "the per-role ambiguity warning is deleted in Story 32-15");
        }
    }

    [Test]
    public async Task GetSystemDefaultPublic_ConfiguredPersonaAbsent_FailsLoud()
    {
        var cpCtx = NewContext();
        var events = new CapturingEvents();
        var agentRepo = new AgentRepository(cpCtx, events);
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(TenantA);
        var factory = new SameDbTenantFactory(_connectionString);
        var selectionRepo = new AgentSelectionRepository(cpCtx, factory, tenantContext);
        var personaOptions = Microsoft.Extensions.Options.Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = "claude" });
        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, new StubMode(TammaMode.SaaS),
            tenantContext, new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            personaOptions, NullLogger<AgentRegistryService>.Instance);

        await using (cpCtx)
        {
            // No persona seeded — fail loud, never an empty/plain fallback.
            Func<Task> act = async () => await registry.GetSystemDefaultPublicAsync("developer");
            (await act.Should().ThrowAsync<TammaError>())
                .Which.Code.Should().Be("AGENT_DEFAULT_PERSONA_MISSING");
        }
    }

    // ── Rollback endpoint (AC 13) ──

    [Test]
    public async Task Rollback_RepointsActiveVersion_ResolvesPrior()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var priv = await s.Agents.CreateAsync(
                new Agent { Name = "atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantA },
                """{ "provider": "anthropic", "model": "v1" }""", "v1", null);
            await s.Agents.PublishVersionAsync(priv.Id, """{ "provider": "openai", "model": "v2" }""", "v2", null);

            var rb = await AgentEndpoints.RollbackVersion(
                priv.Id, new RollbackVersionRequest(1), s.Agents, s.Principal, s.TenantContext, s.Mode);
            var (status, body) = await ExecuteAsync(rb);
            status.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("version").GetInt32().Should().Be(1);

            var reloaded = await s.Agents.GetActiveVersionAsync(priv.Id);
            reloaded!.Version.Should().Be(1);
        }
    }

    [Test]
    public async Task Rollback_UnknownVersion_Returns404()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var priv = await SeedTenantPrivateAsync(s.Agents, TenantA, "developer", "atlas");
            var rb = await AgentEndpoints.RollbackVersion(
                priv.Id, new RollbackVersionRequest(99), s.Agents, s.Principal, s.TenantContext, s.Mode);
            var (status, _) = await ExecuteAsync(rb);
            status.Should().Be(StatusCodes.Status404NotFound);
        }
    }

    [Test]
    public async Task Rollback_PublicAgent_AsNonPlatformAdmin_Returns403()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var pub = await SeedPublicAsync(s.Agents, "developer", "tamma-developer");
            await s.Agents.PublishVersionAsync(pub.Id, Cfg, "v2", null);
            // Caller is a tenant admin (no platformRole claim) — public write forbidden.
            var rb = await AgentEndpoints.RollbackVersion(
                pub.Id, new RollbackVersionRequest(1), s.Agents, s.Principal, s.TenantContext, s.Mode);
            var (status, _) = await ExecuteAsync(rb);
            status.Should().Be(StatusCodes.Status403Forbidden);
        }
    }

    // ── Public-create 403 gate via /api/agents (in-handler IsPlatformAdmin) ──

    [Test]
    public async Task Create_PublicAsTenantAdmin_Returns403_NoRow()
    {
        var s = BuildStack(TammaMode.SaaS, TenantA, null);
        await using (s.Ctx)
        {
            var req = new CreateAgentRequest("tamma-architect", "architect", "public",
                JsonDocument.Parse(Cfg).RootElement.Clone(), null);
            var create = await AgentEndpoints.CreateAgent(
                req, s.Agents, s.Principal, s.TenantContext, s.Mode);
            var (status, body) = await ExecuteAsync(create);
            status.Should().Be(StatusCodes.Status403Forbidden);
            body.GetProperty("error").GetString().Should().Be("forbidden");
            (await s.Ctx.Agents.CountAsync()).Should().Be(0);
        }
    }

    // ── test doubles ──

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    /// <summary>Story 32-15 — supplies the PUBLIC branch's system prompt so
    /// persona resolution succeeds without the full Epic 27 store.</summary>
    private sealed class StubPersonaPrompts : IPersonaPromptResolver
    {
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
            => Task.FromResult($"[persona system prompt for role={role}]");
    }

    private sealed class SameDbTenantFactory(string conn) : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(conn).Options, tenantId));
    }

    /// <summary>
    /// Factory whose context fires <paramref name="afterFirstRead"/> exactly once,
    /// right after the FIRST reader (SELECT) completes — i.e. between the upsert's
    /// existing-row read and its INSERT save. Used to deterministically interleave
    /// a competing writer so the upsert's unique-violation catch path is exercised.
    /// </summary>
    private sealed class BarrierTenantFactory(string conn, Func<Task> afterFirstRead)
        : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseNpgsql(conn)
                .AddInterceptors(new FirstReadBarrierInterceptor(afterFirstRead))
                .Options;
            return ValueTask.FromResult(new TenantDbContext(options, tenantId));
        }

        private sealed class FirstReadBarrierInterceptor(Func<Task> afterFirstRead)
            : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
        {
            private int _fired;

            public override async ValueTask<System.Data.Common.DbDataReader>
                ReaderExecutedAsync(
                    System.Data.Common.DbCommand command,
                    Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
                    System.Data.Common.DbDataReader result,
                    CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await afterFirstRead();
                }
                return result;
            }
        }
    }

    private sealed class CapturingEvents : IEventRepository
    {
        public ConcurrentQueue<DomainEvent> Captured { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Captured.Enqueue(evt); return Task.FromResult(evt); }
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
