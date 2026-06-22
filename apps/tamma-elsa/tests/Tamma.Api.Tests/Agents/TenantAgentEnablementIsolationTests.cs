using System.Collections.Concurrent;
using System.Security.Claims;
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
/// Story 32-16 (AC11) — cross-tenant isolation: a tenant cannot enable/disable
/// for another tenant; A's enablement never appears in or affects B's view.
/// </summary>
[TestFixture]
public class TenantAgentEnablementIsolationTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-3216-1501-3216-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-3216-1501-3216-bbbbbbbbbbbb");

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_enablement_iso_test")
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

    private TenantAgentEnablementService ServiceFor(ControlPlaneDbContext ctx, Guid tenantId, IEventRepository events)
    {
        var agents = new AgentRepository(ctx, events);
        var tenantContext = new TenantContext();
        tenantContext.SetTenantId(tenantId);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var personaOptions = Options.Create(new DefaultPersonaOptions { DefaultPersonaName = "claude" });
        return new TenantAgentEnablementService(
            ctx, agents, events, new StubMode(TammaMode.SaaS), tenantContext, http,
            personaOptions, NullLogger<TenantAgentEnablementService>.Instance);
    }

    [Test]
    public async Task TenantA_Enable_DoesNotAppearIn_TenantB_View()
    {
        await using var ctx = NewContext();
        var events = new CapturingEvents();
        var agents = new AgentRepository(ctx, events);
        var claude = await agents.CreateAsync(
            new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

        await ServiceFor(ctx, TenantA, events).EnableAsync(claude.Id);

        var svcB = ServiceFor(ctx, TenantB, events);
        (await svcB.IsEnabledForPrincipalAsync(claude.Id, Principal.ForTenant(TenantB)))
            .Should().BeFalse("A's enablement must not leak to B");
        (await svcB.ListEnabledPublicAgentIdsAsync(Principal.ForTenant(TenantB)))
            .Should().BeEmpty("B has enabled nothing");

        var viewB = await svcB.ListAsync();
        viewB.Single(v => v.AgentId == claude.Id).Enabled.Should().BeFalse(
            "claude is not enabled for B even though it is for A");
    }

    [Test]
    public async Task TenantA_CannotEnable_TenantB_PrivateAgent_404()
    {
        await using var ctx = NewContext();
        var events = new CapturingEvents();
        var agents = new AgentRepository(ctx, events);
        var foreign = await agents.CreateAsync(
            new Agent { Name = "their-atlas", Role = "developer", Visibility = AgentVisibility.Private, OwnerTenantId = TenantB },
            Cfg, null, null);

        var svcA = ServiceFor(ctx, TenantA, events);
        Func<Task> act = async () => await svcA.EnableAsync(foreign.Id);
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("AGENT.ENABLEMENT.NOT_FOUND");
    }

    [Test]
    public async Task TenantA_Disable_DoesNotAffect_TenantB()
    {
        await using var ctx = NewContext();
        var events = new CapturingEvents();
        var agents = new AgentRepository(ctx, events);
        var claude = await agents.CreateAsync(
            new Agent { Name = "claude", Role = null, Visibility = AgentVisibility.Public }, Cfg, null, null);

        // Both enable it.
        await ServiceFor(ctx, TenantA, events).EnableAsync(claude.Id);
        await ServiceFor(ctx, TenantB, events).EnableAsync(claude.Id);

        // A disables — B is untouched.
        await ServiceFor(ctx, TenantA, events).DisableAsync(claude.Id);

        (await ServiceFor(ctx, TenantA, events)
            .IsEnabledForPrincipalAsync(claude.Id, Principal.ForTenant(TenantA)))
            .Should().BeFalse();
        (await ServiceFor(ctx, TenantB, events)
            .IsEnabledForPrincipalAsync(claude.Id, Principal.ForTenant(TenantB)))
            .Should().BeTrue("B's enablement survives A's disable");
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
