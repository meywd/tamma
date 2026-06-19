using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private Stack BuildStack(TammaMode mode, Guid? tenantId, Guid? userId, bool platformAdmin = false)
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

        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, modeProvider, tenantContext, httpAccessor,
            NullLogger<AgentRegistryService>.Instance);
        var resolver = new AgentResolverService(
            new AgentConfigRepository(factory), null, NullLogger<AgentResolverService>.Instance,
            registry, agentRepo, events);

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

    private sealed class SameDbTenantFactory(string conn) : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(conn).Options, tenantId));
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
