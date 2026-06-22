using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-16 — endpoint-level tests for the /api/agents enablement surface.
/// Drives the <see cref="AgentEndpoints"/> handlers directly (the same delegates
/// Program.cs maps) against a real Postgres testcontainer with the real
/// enablement service. Covers enable/disable → 200, 404 unseen, 409
/// disable-own-private, the list catalog view, and the RBAC contract (the
/// <c>agents:manage</c> permission gate that backs the route's member-403).
/// </summary>
[TestFixture]
public class AgentEnablementEndpointsTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3216-e9d0-3216-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3216-e9d0-3216-bbbbbbbbbbbb");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_enablement_ep_test")
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
            "TRUNCATE tenant_agent_enablements, agent_versions, agents CASCADE;");
    }

    private ControlPlaneDbContext NewContext()
        => new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString).Options);

    private const string Cfg = """{ "provider": "anthropic", "model": "claude-sonnet-4" }""";

    private sealed record Stack(
        TenantAgentEnablementService Service, AgentRepository Agents, ControlPlaneDbContext Ctx);

    private Stack BuildStack(Guid tenantId)
    {
        var ctx = NewContext();
        var events = new CapturingEvents();
        var agents = new AgentRepository(ctx, events);
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(tenantId);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var personaOptions = Options.Create(new DefaultPersonaOptions { DefaultPersonaName = "claude" });
        var svc = new TenantAgentEnablementService(
            ctx, agents, events, new StubMode(TammaMode.SaaS), tenantContext, http,
            personaOptions, NullLogger<TenantAgentEnablementService>.Instance);
        return new Stack(svc, agents, ctx);
    }

    private async Task<Agent> SeedPersonaAsync(AgentRepository repo, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

    private async Task<Agent> SeedTenantPrivateAsync(AgentRepository repo, Guid tenantId, string name)
        => await repo.CreateAsync(
            new Agent { Name = name, Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = tenantId },
            Cfg, null, null);

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

    // ── PUT enable / disable ──

    [Test]
    public async Task SetEnablement_EnablePublicPersona_Returns200()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            var result = await AgentEndpoints.SetEnablement(claude.Id, new SetEnablementRequest(true), s.Service);
            var (status, body) = await ExecuteAsync(result);
            status.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("agentId").GetGuid().Should().Be(claude.Id);
            body.GetProperty("enabled").GetBoolean().Should().BeTrue();
            body.GetProperty("personaName").GetString().Should().Be("claude");
        }
    }

    [Test]
    public async Task SetEnablement_DisableViaFalse_Returns200()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);
            var result = await AgentEndpoints.SetEnablement(claude.Id, new SetEnablementRequest(false), s.Service);
            var (status, body) = await ExecuteAsync(result);
            status.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("enabled").GetBoolean().Should().BeFalse();
        }
    }

    [Test]
    public async Task SetEnablement_UnseenTarget_Returns404()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var foreign = await SeedTenantPrivateAsync(s.Agents, TenantB, "their-atlas");
            var result = await AgentEndpoints.SetEnablement(foreign.Id, new SetEnablementRequest(true), s.Service);
            var (status, body) = await ExecuteAsync(result);
            status.Should().Be(StatusCodes.Status404NotFound);
            body.GetProperty("error").GetString().Should().Be("agent_not_found");
        }
    }

    [Test]
    public async Task SetEnablement_DisableOwnPrivate_Returns409()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var atlas = await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas");
            var result = await AgentEndpoints.SetEnablement(atlas.Id, new SetEnablementRequest(false), s.Service);
            var (status, body) = await ExecuteAsync(result);
            status.Should().Be(StatusCodes.Status409Conflict);
            body.GetProperty("error").GetString().Should().Be("private_not_disableable");
        }
    }

    // ── DELETE disable ──

    [Test]
    public async Task DisableEnablement_PublicPersona_Returns200()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await s.Service.EnableAsync(claude.Id);
            var result = await AgentEndpoints.DisableEnablement(claude.Id, s.Service);
            var (status, body) = await ExecuteAsync(result);
            status.Should().Be(StatusCodes.Status200OK);
            body.GetProperty("enabled").GetBoolean().Should().BeFalse();
        }
    }

    [Test]
    public async Task DisableEnablement_OwnPrivate_Returns409()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var atlas = await SeedTenantPrivateAsync(s.Agents, TenantA, "atlas");
            var result = await AgentEndpoints.DisableEnablement(atlas.Id, s.Service);
            var (status, _) = await ExecuteAsync(result);
            status.Should().Be(StatusCodes.Status409Conflict);
        }
    }

    // ── GET list ──

    [Test]
    public async Task ListEnablement_ReturnsCatalogView()
    {
        var s = BuildStack(TenantA);
        await using (s.Ctx)
        {
            var claude = await SeedPersonaAsync(s.Agents, "claude");
            await SeedPersonaAsync(s.Agents, "gemini");
            await s.Service.EnableAsync(claude.Id);

            var (status, body) = await ExecuteAsync(await AgentEndpoints.ListEnablement(s.Service));
            status.Should().Be(StatusCodes.Status200OK);
            var rows = body.EnumerateArray().ToList();
            rows.Should().HaveCount(2);
            rows.Single(r => r.GetProperty("personaName").GetString() == "claude")
                .GetProperty("enabled").GetBoolean().Should().BeTrue();
            rows.Single(r => r.GetProperty("personaName").GetString() == "gemini")
                .GetProperty("enabled").GetBoolean().Should().BeFalse();
        }
    }

    // ── RBAC contract (the agents:manage gate backing the route's member-403) ──

    [Test]
    public void EnablementWrites_GatedBy_AgentsManage_MemberDenied_AdminOwnerAllowed()
    {
        // The PUT/DELETE routes are .RequireAuthorization("AgentManage") =
        // agents:manage. Prove the underlying permission excludes member and
        // admits admin + owner (the route's member-403 contract, AC6).
        Permissions.HasPermission("member", "agents:manage").Should().BeFalse(
            "members cannot enable/disable — reads only");
        Permissions.HasPermission("admin", "agents:manage").Should().BeTrue();
        Permissions.HasPermission("owner", "agents:manage").Should().BeTrue();
    }

    private sealed class StubMode(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private sealed class CapturingEvents : IEventRepository
    {
        private readonly ConcurrentQueue<DomainEvent> _captured = new();
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
