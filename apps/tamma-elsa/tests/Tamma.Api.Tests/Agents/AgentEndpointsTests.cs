using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-1 (Task 5) — endpoint + RBAC + cross-tenant-isolation tests for the
/// first-class agent surface on <see cref="AgentEndpoints"/>. Drives the public
/// endpoint methods directly against constructed
/// <see cref="ClaimsPrincipal"/> / <see cref="ITammaModeProvider"/> stubs (the
/// same delegates Program.cs wires into the minimal API) backed by a real
/// Postgres testcontainer — dev-mode permissive auth short-circuits named
/// policies, so HTTP-level RBAC is pinned via the <see cref="Permissions"/>
/// matrix (see <see cref="AgentManagePermissionTests"/>) and per-mode + per-
/// tenant handling is pinned by direct invocation.
/// </summary>
[TestFixture]
public class AgentEndpointsTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-32e1-32e1-32e1-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-32e1-32e1-32e1-bbbbbbbbbbbb");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_endpoint_test")
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

    private (IAgentRepository repo, ControlPlaneDbContext ctx) BuildRepo()
    {
        var ctx = NewContext();
        return (new AgentRepository(ctx, new CapturingEventRepository()), ctx);
    }

    private static ITammaModeProvider Mode(TammaMode m) => new StubModeProvider(m);

    private sealed class StubModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private static TenantContext TenantCtx(Guid? tenantId = null)
    {
        var tc = new TenantContext();
        if (tenantId is Guid t) tc.SetTenantId(t);
        return tc;
    }

    private static ClaimsPrincipal Principal(Guid? userId = null, bool platformAdmin = false)
    {
        var claims = new List<Claim>();
        if (userId is Guid uid) claims.Add(new Claim(ClaimTypes.NameIdentifier, uid.ToString()));
        if (platformAdmin) claims.Add(new Claim("platformRole", "platform_admin"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static JsonElement Config(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement ValidConfig =
        Config("""{ "provider": "anthropic", "model": "claude-sonnet-4" }""");

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

    // ── Create + round-trip ──

    [Test]
    public async Task CreateAgent_Private_SaaS_SetsTenantOwner_RoundTrips()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var admin = Principal(Guid.NewGuid());
            var req = new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null);

            var create = await AgentEndpoints.CreateAgent(
                req, repo, admin, TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, body) = await ExecuteAsync(create);

            status.Should().Be(StatusCodes.Status201Created);
            var id = body.GetProperty("id").GetGuid();
            body.GetProperty("currentVersion").GetInt32().Should().Be(1);

            var agent = await repo.GetByIdAsync(id);
            agent!.OwnerTenantId.Should().Be(TenantA);
            agent.OwnerUserId.Should().BeNull();
            agent.Visibility.Should().Be(AgentVisibility.Private);
        }
    }

    [Test]
    public async Task CreateAgent_Private_SingleUser_SetsUserOwner()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var userId = Guid.NewGuid();
            var req = new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null);

            var create = await AgentEndpoints.CreateAgent(
                req, repo, Principal(userId), TenantCtx(), Mode(TammaMode.SingleUser));
            var (status, body) = await ExecuteAsync(create);

            status.Should().Be(StatusCodes.Status201Created);
            var agent = await repo.GetByIdAsync(body.GetProperty("id").GetGuid());
            agent!.OwnerUserId.Should().Be(userId);
            agent.OwnerTenantId.Should().BeNull();
        }
    }

    [Test]
    public async Task CreateAgent_Public_AsPlatformAdmin_Succeeds()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var req = new CreateAgentRequest("tamma-architect", "architect", "public", ValidConfig, null);
            var create = await AgentEndpoints.CreateAgent(
                req, repo, Principal(Guid.NewGuid(), platformAdmin: true),
                TenantCtx(), Mode(TammaMode.SaaS));
            var (status, _) = await ExecuteAsync(create);
            status.Should().Be(StatusCodes.Status201Created);
        }
    }

    [Test]
    public async Task CreateAgent_Public_AsNonPlatformAdmin_Returns403()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var req = new CreateAgentRequest("tamma-architect", "architect", "public", ValidConfig, null);
            // tenant admin (no platformRole claim) — public create is platform-only.
            var create = await AgentEndpoints.CreateAgent(
                req, repo, Principal(Guid.NewGuid()), TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, _) = await ExecuteAsync(create);
            status.Should().Be(StatusCodes.Status403Forbidden);

            (await ctx.Agents.CountAsync()).Should().Be(0, "no row on a rejected public create");
        }
    }

    [Test]
    public async Task CreateAgent_InvalidConfig_Returns400_NoRow()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var req = new CreateAgentRequest(
                "atlas", "architect", "private",
                Config("""{ "temperature": 9 }"""), null);
            var create = await AgentEndpoints.CreateAgent(
                req, repo, Principal(Guid.NewGuid()), TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, _) = await ExecuteAsync(create);

            status.Should().Be(StatusCodes.Status400BadRequest);
            (await ctx.Agents.CountAsync()).Should().Be(0);
        }
    }

    [Test]
    public async Task CreateAgent_InvalidRole_Returns400()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var req = new CreateAgentRequest("x", "wizard", "private", ValidConfig, null);
            var create = await AgentEndpoints.CreateAgent(
                req, repo, Principal(Guid.NewGuid()), TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, _) = await ExecuteAsync(create);
            status.Should().Be(StatusCodes.Status400BadRequest);
        }
    }

    [Test]
    public async Task CreateAgent_DuplicatePublicHandle_Returns409()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var pa = Principal(Guid.NewGuid(), platformAdmin: true);
            var req = new CreateAgentRequest("tamma-architect", "architect", "public", ValidConfig, null);

            await ExecuteAsync(await AgentEndpoints.CreateAgent(req, repo, pa, TenantCtx(), Mode(TammaMode.SaaS)));
            var (status, _) = await ExecuteAsync(
                await AgentEndpoints.CreateAgent(req, repo, pa, TenantCtx(), Mode(TammaMode.SaaS)));

            status.Should().Be(StatusCodes.Status409Conflict);
        }
    }

    // ── Publish + Get round-trip ──

    [Test]
    public async Task PublishVersion_Then_GetVersion_RoundTrips()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var admin = Principal(Guid.NewGuid());
            var created = await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null),
                repo, admin, TenantCtx(TenantA), Mode(TammaMode.SaaS)));
            var id = created.Body.GetProperty("id").GetGuid();

            var pub = await AgentEndpoints.PublishVersion(
                id, new PublishVersionRequest(Config("""{ "model": "v2" }"""), "second"),
                repo, admin, TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (pubStatus, pubBody) = await ExecuteAsync(pub);
            pubStatus.Should().Be(StatusCodes.Status200OK);
            pubBody.GetProperty("version").GetInt32().Should().Be(2);

            var get = await AgentEndpoints.GetVersion(
                id, 2, repo, admin, TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (getStatus, getBody) = await ExecuteAsync(get);
            getStatus.Should().Be(StatusCodes.Status200OK);
            getBody.GetProperty("config").GetProperty("model").GetString().Should().Be("v2");
        }
    }

    // ── List = public ∪ own private ──

    [Test]
    public async Task ListAgents_Returns_Public_Union_OwnPrivate()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var pa = Principal(Guid.NewGuid(), platformAdmin: true);
            var adminA = Principal(Guid.NewGuid());
            await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("tamma-architect", "architect", "public", ValidConfig, null),
                repo, pa, TenantCtx(), Mode(TammaMode.SaaS)));
            await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas-a", "architect", "private", ValidConfig, null),
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS)));
            await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas-b", "architect", "private", ValidConfig, null),
                repo, Principal(Guid.NewGuid()), TenantCtx(TenantB), Mode(TammaMode.SaaS)));

            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, body) = await ExecuteAsync(list);
            status.Should().Be(StatusCodes.Status200OK);

            var names = body.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
            names.Should().Contain("tamma-architect");
            names.Should().Contain("atlas-a");
            names.Should().NotContain("atlas-b", "tenant A must not see tenant B's private agent");
        }
    }

    // ── Story 32-2 AC5 — list filters (?role=&visibility=&status=) ──

    /// <summary>
    /// Seed a fixed mix for the filter tests: one public + several own-private
    /// (different roles, one archived) for tenant A, plus a tenant-B private the
    /// caller must NEVER see. Returns the tenant-A admin principal.
    /// </summary>
    private async Task<ClaimsPrincipal> SeedFilterFixtureAsync(IAgentRepository repo)
    {
        var pa = Principal(Guid.NewGuid(), platformAdmin: true);
        var adminA = Principal(Guid.NewGuid());

        // public architect (visible to everyone)
        await ExecuteAsync(await AgentEndpoints.CreateAgent(
            new CreateAgentRequest("pub-architect", "architect", "public", ValidConfig, null),
            repo, pa, TenantCtx(), Mode(TammaMode.SaaS)));

        // own-private architect (active)
        await ExecuteAsync(await AgentEndpoints.CreateAgent(
            new CreateAgentRequest("a-architect", "architect", "private", ValidConfig, null),
            repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS)));

        // own-private developer (active)
        await ExecuteAsync(await AgentEndpoints.CreateAgent(
            new CreateAgentRequest("a-developer", "developer", "private", ValidConfig, null),
            repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS)));

        // own-private tester, then archived
        var testerCreated = await ExecuteAsync(await AgentEndpoints.CreateAgent(
            new CreateAgentRequest("a-tester", "tester", "private", ValidConfig, null),
            repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS)));
        await ExecuteAsync(await AgentEndpoints.ArchiveAgent(
            testerCreated.Body.GetProperty("id").GetGuid(), repo, adminA,
            TenantCtx(TenantA), Mode(TammaMode.SaaS)));

        // tenant-B private architect — must never surface for tenant A
        await ExecuteAsync(await AgentEndpoints.CreateAgent(
            new CreateAgentRequest("b-architect", "architect", "private", ValidConfig, null),
            repo, Principal(Guid.NewGuid()), TenantCtx(TenantB), Mode(TammaMode.SaaS)));

        return adminA;
    }

    private static List<string> Names(JsonElement body)
        => body.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();

    [Test]
    public async Task ListAgents_NoFilters_StillReturns_Public_Union_OwnPrivate()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            var names = Names(body);
            names.Should().BeEquivalentTo(
                new[] { "pub-architect", "a-architect", "a-developer", "a-tester" });
            names.Should().NotContain("b-architect");
        }
    }

    [Test]
    public async Task ListAgents_FilterByRole_ReturnsOnlyThatRole()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), role: "developer");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            Names(body).Should().BeEquivalentTo(new[] { "a-developer" });
        }
    }

    [Test]
    public async Task ListAgents_FilterByRole_NeverSurfacesAnotherTenantsPrivate()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            // architect role matches a-architect (own private), pub-architect
            // (public), AND b-architect (tenant B private) — but B must be scoped out.
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), role: "architect");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            var names = Names(body);
            names.Should().BeEquivalentTo(new[] { "pub-architect", "a-architect" });
            names.Should().NotContain("b-architect",
                "role filter must apply AFTER visibility scoping — never widen to another tenant");
        }
    }

    [Test]
    public async Task ListAgents_FilterByVisibilityPrivate_ReturnsOnlyOwnPrivate()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), visibility: "private");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            var names = Names(body);
            // own private only — never public, never tenant B's private.
            names.Should().BeEquivalentTo(new[] { "a-architect", "a-developer", "a-tester" });
            names.Should().NotContain("pub-architect");
            names.Should().NotContain("b-architect");
        }
    }

    [Test]
    public async Task ListAgents_FilterByVisibilityPublic_ReturnsOnlyPublic()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), visibility: "public");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            Names(body).Should().BeEquivalentTo(new[] { "pub-architect" });
        }
    }

    [Test]
    public async Task ListAgents_FilterByStatusArchived_ReturnsOnlyArchived()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), status: "archived");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            Names(body).Should().BeEquivalentTo(new[] { "a-tester" });
        }
    }

    [Test]
    public async Task ListAgents_FilterByStatusActive_ExcludesArchived()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), status: "active");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            var names = Names(body);
            names.Should().BeEquivalentTo(new[] { "pub-architect", "a-architect", "a-developer" });
            names.Should().NotContain("a-tester");
        }
    }

    [Test]
    public async Task ListAgents_CombinedFilters_AndTogether()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            // private AND architect AND active → only a-architect.
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS),
                role: "architect", visibility: "private", status: "active");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            Names(body).Should().BeEquivalentTo(new[] { "a-architect" });
        }
    }

    [Test]
    public async Task ListAgents_UnknownRole_Returns400()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), role: "wizard");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status400BadRequest);
            body.GetProperty("error").GetString().Should().Be("invalid_role");
        }
    }

    [Test]
    public async Task ListAgents_UnknownVisibility_Returns400()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), visibility: "secret");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status400BadRequest);
            body.GetProperty("error").GetString().Should().Be("invalid_visibility");
        }
    }

    [Test]
    public async Task ListAgents_UnknownStatus_Returns400()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), status: "deleted");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status400BadRequest);
            body.GetProperty("error").GetString().Should().Be("invalid_status");
        }
    }

    [Test]
    public async Task ListAgents_EmptyFilterStrings_TreatedAsNoFilter()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            // Empty/whitespace params must NOT 400 and must NOT narrow.
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS),
                role: "", visibility: "  ", status: null);
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            Names(body).Should().BeEquivalentTo(
                new[] { "pub-architect", "a-architect", "a-developer", "a-tester" });
        }
    }

    [Test]
    public async Task ListAgents_FilterByRole_NormalizesLegacyAlias()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var adminA = await SeedFilterFixtureAsync(repo);
            // 'implementer' is a legacy alias for 'developer' (RolePhaseMap).
            var list = await AgentEndpoints.ListAgents(
                repo, adminA, TenantCtx(TenantA), Mode(TammaMode.SaaS), role: "implementer");
            var (status, body) = await ExecuteAsync(list);

            status.Should().Be(StatusCodes.Status200OK);
            Names(body).Should().BeEquivalentTo(new[] { "a-developer" });
        }
    }

    // ── Cross-tenant isolation: GET {id} for another tenant's private → 404 ──

    [Test]
    public async Task GetAgent_OtherTenantsPrivate_Returns404_NotForbidden()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var created = await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null),
                repo, Principal(Guid.NewGuid()), TenantCtx(TenantA), Mode(TammaMode.SaaS)));
            var aAtlasId = created.Body.GetProperty("id").GetGuid();

            // Tenant B fetches tenant A's private agent → 404 (not 403).
            var get = await AgentEndpoints.GetAgent(
                aAtlasId, repo, Principal(Guid.NewGuid()), TenantCtx(TenantB), Mode(TammaMode.SaaS));
            var (status, _) = await ExecuteAsync(get);
            status.Should().Be(StatusCodes.Status404NotFound);
        }
    }

    [Test]
    public async Task PublishVersion_OnOtherTenantsPrivate_Returns404()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var created = await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null),
                repo, Principal(Guid.NewGuid()), TenantCtx(TenantA), Mode(TammaMode.SaaS)));
            var aAtlasId = created.Body.GetProperty("id").GetGuid();

            var pub = await AgentEndpoints.PublishVersion(
                aAtlasId, new PublishVersionRequest(ValidConfig, null),
                repo, Principal(Guid.NewGuid()), TenantCtx(TenantB), Mode(TammaMode.SaaS));
            var (status, _) = await ExecuteAsync(pub);
            status.Should().Be(StatusCodes.Status404NotFound,
                "an un-owned private agent must 404 (not 403) on write — don't leak existence");
        }
    }

    [Test]
    public async Task TwoTenants_MayEachOwn_PrivateAtlas_ViaEndpoint()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var a = await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null),
                repo, Principal(Guid.NewGuid()), TenantCtx(TenantA), Mode(TammaMode.SaaS)));
            var b = await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null),
                repo, Principal(Guid.NewGuid()), TenantCtx(TenantB), Mode(TammaMode.SaaS)));

            a.Status.Should().Be(StatusCodes.Status201Created);
            b.Status.Should().Be(StatusCodes.Status201Created,
                "the per-owner partial index allows both tenants to own a private 'atlas'");
        }
    }

    // ── Archive ──

    [Test]
    public async Task ArchiveAgent_OwnPrivate_Returns200()
    {
        var (repo, ctx) = BuildRepo();
        await using (ctx)
        {
            var admin = Principal(Guid.NewGuid());
            var created = await ExecuteAsync(await AgentEndpoints.CreateAgent(
                new CreateAgentRequest("atlas", "architect", "private", ValidConfig, null),
                repo, admin, TenantCtx(TenantA), Mode(TammaMode.SaaS)));
            var id = created.Body.GetProperty("id").GetGuid();

            var arch = await AgentEndpoints.ArchiveAgent(
                id, repo, admin, TenantCtx(TenantA), Mode(TammaMode.SaaS));
            var (status, body) = await ExecuteAsync(arch);
            status.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("status").GetString().Should().Be("archived");
        }
    }

    /// <summary>Capturing event repo (no real tenant DB needed for endpoint tests).</summary>
    private sealed class CapturingEventRepository : IEventRepository
    {
        private readonly ConcurrentQueue<DomainEvent> _events = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            _events.Enqueue(evt);
            return Task.FromResult(evt);
        }
        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) =>
            Task.FromResult(_events.ToList());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) =>
            Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)_events.ToList(), _events.Count));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) =>
            Task.FromResult(((IReadOnlyList<DomainEvent>)_events.ToList(), _events.Count));
    }
}

/// <summary>
/// Story 32-1 — pins the <c>agents:manage</c> permission contract added to the
/// role matrix. CLAUDE.md "Prompt Store Architecture / RBAC" (agents follow the
/// same tenant-scoped RBAC) requires create/publish/archive of a PRIVATE agent
/// to be reachable by tenant_owner OR tenant_admin; member-role callers get 403.
/// </summary>
[TestFixture]
public class AgentManagePermissionTests
{
    [Test]
    public void Owner_CanManageAgents()
        => Permissions.HasPermission("owner", "agents:manage").Should().BeTrue();

    [Test]
    public void Admin_CanManageAgents()
        => Permissions.HasPermission("admin", "agents:manage").Should().BeTrue(
            "private agent create/publish/archive must be reachable by tenant_admin");

    [Test]
    public void Member_CannotManageAgents()
        => Permissions.HasPermission("member", "agents:manage").Should().BeFalse(
            "member users get 403 on agent writes in SaaS mode");

    [Test]
    public void GetRolePermissions_Member_ExcludesAgentsManage()
        => Permissions.GetRolePermissions("member").Should().NotContain("agents:manage");

    [Test]
    public void GetRolePermissions_Admin_IncludesAgentsManage()
        => Permissions.GetRolePermissions("admin").Should().Contain("agents:manage");
}
