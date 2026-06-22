using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Auth;
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
/// Story 32-18 — endpoint-level mapping for the enablement gate. Drives the
/// <see cref="AgentEndpoints"/> handlers directly against a real Postgres
/// testcontainer with the real registry/resolver + a FAKED
/// <see cref="ITenantAgentEnablementReader"/>. Covers: PUT role-selection at a
/// disabled persona → 409 agent_not_enabled; Resolve with nothing enabled → 404
/// agent_no_enabled_default; GET /api/agents member view = enabled∪own-private;
/// ?includeDisabled=true admin view = full catalog + enabled flags; member
/// ?includeDisabled ignored.
/// </summary>
[TestFixture]
public class AgentEnablementGateEndpointsTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3218-e9d0-3218-aaaaaaaaaaaa");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_enablement_gate_ep_test")
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
        Guid? tenantId, bool admin, FakeEnablement enablement, string defaultPersonaName = "claude")
    {
        var cpCtx = NewContext();
        var events = new CapturingEvents();
        var agentRepo = new AgentRepository(cpCtx, events);

        var tenantContext = new TenantContext();
        if (tenantId is Guid tid) tenantContext.SetTenantId(tid);

        var factory = new SameDbTenantFactory(_connectionString);
        var selectionRepo = new AgentSelectionRepository(cpCtx, factory, tenantContext);
        var modeProvider = new StubMode(TammaMode.SaaS);
        var principal = Principal(admin);
        var httpAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };

        var personaOptions = Microsoft.Extensions.Options.Options.Create(
            new DefaultPersonaOptions { DefaultPersonaName = defaultPersonaName });

        var registry = new AgentRegistryService(
            agentRepo, selectionRepo, events, modeProvider, tenantContext, httpAccessor,
            personaOptions, enablement, NullLogger<AgentRegistryService>.Instance);
        var resolver = new AgentResolverService(
            new AgentConfigRepository(factory), null, NullLogger<AgentResolverService>.Instance,
            registry, agentRepo, events, null, new StubPersonaPrompts());

        return new Stack(agentRepo, registry, resolver, principal, enablement, tenantContext, cpCtx);
    }

    private sealed record Stack(
        AgentRepository Agents,
        AgentRegistryService Registry,
        AgentResolverService Resolver,
        ClaimsPrincipal Principal,
        FakeEnablement Enablement,
        TenantContext TenantContext,
        ControlPlaneDbContext Ctx);

    private static ClaimsPrincipal Principal(bool admin)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        // The role claim drives Permissions.HasPermission("agents:manage").
        claims.Add(new Claim(ClaimTypes.Role, admin ? "admin" : "member"));
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

    private async Task<Agent> SeedPersonaAsync(AgentRepository repo, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

    private async Task<Agent> SeedTenantPrivateAsync(AgentRepository repo, Guid tenantId, string role, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = role, Visibility = AgentVisibility.Private, OwnerTenantId = tenantId },
            Cfg, null, null);

    // ── PUT role-selection at a disabled persona → 409 agent_not_enabled ──

    [Test]
    public async Task SelectForRole_DisabledPersona_Returns409_AgentNotEnabled()
    {
        var enablement = new FakeEnablement();
        var s = BuildStack(TenantA, admin: true, enablement);
        await using (s.Ctx)
        {
            var persona = await SeedPersonaAsync(s.Agents, "claude"); // NOT enabled

            var result = await AgentEndpoints.SelectForRole(
                "developer", new SelectRoleRequest(persona.Id), s.Registry, s.Principal);
            var (status, body) = await ExecuteAsync(result);

            status.Should().Be(StatusCodes.Status409Conflict);
            body.GetProperty("error").GetString().Should().Be("agent_not_enabled");
        }
    }

    [Test]
    public async Task SelectForRole_EnabledPersona_Returns200()
    {
        var enablement = new FakeEnablement();
        var s = BuildStack(TenantA, admin: true, enablement);
        await using (s.Ctx)
        {
            var persona = await SeedPersonaAsync(s.Agents, "claude");
            enablement.Enable(persona.Id);

            var result = await AgentEndpoints.SelectForRole(
                "developer", new SelectRoleRequest(persona.Id), s.Registry, s.Principal);
            var (status, _) = await ExecuteAsync(result);

            status.Should().Be(StatusCodes.Status200OK);
        }
    }

    // ── Resolve with nothing enabled → 404 agent_no_enabled_default ──

    [Test]
    public async Task Resolve_NothingEnabled_Returns404_NoEnabledDefault()
    {
        var enablement = new FakeEnablement();
        var s = BuildStack(TenantA, admin: true, enablement);
        await using (s.Ctx)
        {
            await SeedPersonaAsync(s.Agents, "claude"); // seeded, NOT enabled

            var (status, body) = await ExecuteAsync(
                await AgentEndpoints.Resolve("developer", null, s.Resolver));

            status.Should().Be(StatusCodes.Status404NotFound);
            body.GetProperty("error").GetString().Should().Be("agent_no_enabled_default");
        }
    }

    // ── GET /api/agents — member view = enabled(public) ∪ own-private ──

    [Test]
    public async Task ListAgents_Member_ReturnsOnlyEnabledPublic_PlusOwnPrivate()
    {
        var enablement = new FakeEnablement();
        var s = BuildStack(TenantA, admin: false, enablement);
        await using (s.Ctx)
        {
            var enabled = await SeedPersonaAsync(s.Agents, "claude");
            var disabled = await SeedPersonaAsync(s.Agents, "gemini");
            var ownPriv = await SeedTenantPrivateAsync(s.Agents, TenantA, "developer", "atlas");
            enablement.Enable(enabled.Id);

            var (status, body) = await ExecuteAsync(await AgentEndpoints.ListAgents(
                s.Agents, enablement, s.Principal, s.TenantContext, new StubMode(TammaMode.SaaS)));

            status.Should().Be(StatusCodes.Status200OK);
            var ids = body.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
            ids.Should().Contain(enabled.Id);
            ids.Should().Contain(ownPriv.Id);
            ids.Should().NotContain(disabled.Id, "members never see disabled public personas");
        }
    }

    // ── ?includeDisabled=true admin view = full catalog + enabled flags ──

    [Test]
    public async Task ListAgents_AdminIncludeDisabled_ReturnsFullCatalog_WithEnabledFlags()
    {
        var enablement = new FakeEnablement();
        var s = BuildStack(TenantA, admin: true, enablement);
        await using (s.Ctx)
        {
            var enabled = await SeedPersonaAsync(s.Agents, "claude");
            var disabled = await SeedPersonaAsync(s.Agents, "gemini");
            enablement.Enable(enabled.Id);

            var (status, body) = await ExecuteAsync(await AgentEndpoints.ListAgents(
                s.Agents, enablement, s.Principal, s.TenantContext, new StubMode(TammaMode.SaaS),
                includeDisabled: true));

            status.Should().Be(StatusCodes.Status200OK);
            var rows = body.EnumerateArray().ToList();
            var ids = rows.Select(e => e.GetProperty("id").GetGuid()).ToList();
            ids.Should().Contain(enabled.Id);
            ids.Should().Contain(disabled.Id, "admins see the full catalog incl. disabled");

            bool EnabledFlag(Guid id) => rows
                .Single(e => e.GetProperty("id").GetGuid() == id)
                .GetProperty("enabled").GetBoolean();
            EnabledFlag(enabled.Id).Should().BeTrue();
            EnabledFlag(disabled.Id).Should().BeFalse();
        }
    }

    [Test]
    public async Task ListAgents_MemberIncludeDisabled_Ignored_StillHidesDisabled()
    {
        var enablement = new FakeEnablement();
        var s = BuildStack(TenantA, admin: false, enablement);
        await using (s.Ctx)
        {
            var enabled = await SeedPersonaAsync(s.Agents, "claude");
            var disabled = await SeedPersonaAsync(s.Agents, "gemini");
            enablement.Enable(enabled.Id);

            var (status, body) = await ExecuteAsync(await AgentEndpoints.ListAgents(
                s.Agents, enablement, s.Principal, s.TenantContext, new StubMode(TammaMode.SaaS),
                includeDisabled: true));

            status.Should().Be(StatusCodes.Status200OK);
            var ids = body.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
            ids.Should().Contain(enabled.Id);
            ids.Should().NotContain(disabled.Id, "?includeDisabled is admin-only; member view stays gated");
        }
    }

    // ── test doubles ──

    private sealed class FakeEnablement : ITenantAgentEnablementReader
    {
        private readonly HashSet<Guid> _enabled = new();
        public void Enable(Guid id) => _enabled.Add(id);

        public Task<bool> IsEnabledForPrincipalAsync(Guid agentId, Principal principal, CancellationToken ct = default)
            => Task.FromResult(_enabled.Contains(agentId));
        public Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(Principal principal, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<Guid>)_enabled.ToList());
        public Task<Guid?> GetEnabledDefaultPersonaIdAsync(Principal principal, CancellationToken ct = default)
            => Task.FromResult(_enabled.Count == 1 ? _enabled.Single() : (Guid?)null);
    }

    private sealed class StubPersonaPrompts : IPersonaPromptResolver
    {
        public Task<string> ResolveAsync(
            Principal principal, string role, string? action, CancellationToken ct = default)
            => Task.FromResult($"[persona system prompt for role={role}]");
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
