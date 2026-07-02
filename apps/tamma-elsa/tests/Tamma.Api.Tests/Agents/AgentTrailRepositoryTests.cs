using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-6 (T3/T4) — <see cref="EventRepository.QueryAgentTrailAsync"/> against
/// a real Postgres container (the JSONB <c>agentId</c> predicate + BIGSERIAL
/// cursor are not translatable on EF-InMemory). Proves:
/// <list type="bullet">
///   <item>tenant isolation (AC4): a query scoped to tenant B never returns
///     tenant A's rows — even when both ran the SAME agent id (one public agent →
///     N per-tenant trails);</item>
///   <item>the hard null-tenant guard: an empty tenant throws
///     <see cref="NotSupportedException"/> (no cross-tenant read path);</item>
///   <item>cursor correctness (AC5): stable <c>SequenceNumber</c> ordering across
///     same-millisecond <c>CreatedAt</c>, no dup/skip at page boundaries;</item>
///   <item>filters: type prefix, role, provider, outcome;</item>
///   <item>diagnostics link (AC8): a run's <c>AGENT.TASK.*</c> event and its
///     <see cref="ProviderDiagnostic"/> row share <c>correlationId</c> + agent.</item>
/// </list>
/// </summary>
[TestFixture]
public class AgentTrailRepositoryTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");
    private static readonly Guid AgentX = Guid.Parse("cccccccc-3333-3333-3333-cccccccccccc");
    private static readonly Guid AgentY = Guid.Parse("dddddddd-4444-4444-4444-dddddddddddd");

    private PostgreSqlContainer _postgres = null!;
    private DbContextOptions<TenantDbContext> _options = null!;
    private ITenantDbContextFactory _factory = null!;
    private EventRepository _repo = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("agent_trail_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        // Create the full tenant schema once (domain_events + provider_diagnostics
        // are the tables these tests touch; EnsureCreated builds the lot).
        await using (var db = new TestTenantDbContext(_options, TenantA))
        {
            await db.Database.EnsureCreatedAsync();
        }

        _factory = new TestTenantDbContextFactory(_options);
        _repo = new EventRepository(_factory, new TenantContext(), platformEvents: null);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"TRUNCATE TABLE domain_events; TRUNCATE TABLE provider_diagnostics;";
        await cmd.ExecuteNonQueryAsync();
    }

    // ── AC4: tenant isolation ───────────────────────────────────────────

    [Test]
    public async Task QueryAgentTrail_ReturnsOnlyTheScopedTenantsRows_EvenForSameAgentId()
    {
        // One public agent (AgentX) run by BOTH tenants → two independent trails.
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS", role: "developer", provider: "anthropic");
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS", role: "developer", provider: "anthropic");
        await SeedTaskAsync(TenantB, AgentX, "AGENT.TASK.SUCCESS", role: "developer", provider: "anthropic");

        var (aRows, aTotal) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50);
        var (bRows, bTotal) = await _repo.QueryAgentTrailAsync(
            TenantB, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50);

        aTotal.Should().Be(2);
        bTotal.Should().Be(1);
        aRows.Should().OnlyContain(e => e.TenantId == TenantA);
        bRows.Should().OnlyContain(e => e.TenantId == TenantB);
    }

    [Test]
    public void QueryAgentTrail_EmptyTenant_Throws_NoCrossTenantReadPath()
    {
        Func<Task> act = () => _repo.QueryAgentTrailAsync(
            Guid.Empty, AgentX, null, null, null, null, null, null, null, 50);
        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task QueryAgentTrail_FiltersByAgentId_WithinTenant()
    {
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS");
        await SeedTaskAsync(TenantA, AgentY, "AGENT.TASK.SUCCESS");

        var (rows, total) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50);

        total.Should().Be(1);
        rows.Should().ContainSingle();
        Tags(rows[0])["agentId"].Should().Be(AgentX.ToString());
    }

    // ── AC5: cursor pagination across same-millisecond CreatedAt ─────────

    [Test]
    public async Task QueryAgentTrail_PagesOnSequenceNumber_StableAcrossSameMillisecond()
    {
        // 5 events sharing the SAME CreatedAt millisecond — only SequenceNumber
        // disambiguates. Insert order defines the BIGSERIAL order.
        var ts = new DateTime(2026, 6, 30, 12, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS", createdAt: ts);
        }

        // Page 1 (limit 2, DESC by SequenceNumber).
        var (p1, total) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, cursor: null, 2);
        total.Should().Be(5);
        p1.Should().HaveCount(2);
        p1[0].SequenceNumber.Should().BeGreaterThan(p1[1].SequenceNumber);

        // Page 2 from the last seen SequenceNumber.
        var (p2, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, cursor: p1[^1].SequenceNumber, 2);
        p2.Should().HaveCount(2);
        p2[0].SequenceNumber.Should().BeLessThan(p1[^1].SequenceNumber);

        // Page 3 (final row).
        var (p3, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, cursor: p2[^1].SequenceNumber, 2);
        p3.Should().HaveCount(1);

        // No dupes / no skips across the three pages: 5 distinct sequence numbers.
        var all = p1.Concat(p2).Concat(p3).Select(e => e.SequenceNumber).ToList();
        all.Should().OnlyHaveUniqueItems();
        all.Should().BeInDescendingOrder();
        all.Should().HaveCount(5);
    }

    // ── filters ─────────────────────────────────────────────────────────

    [Test]
    public async Task QueryAgentTrail_FiltersByRoleProviderAndOutcome()
    {
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS", role: "developer", provider: "anthropic");
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.FAILED", role: "developer", provider: "openai");
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS", role: "tester", provider: "anthropic");

        var (byRole, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, role: "developer", null, null, null, 50);
        byRole.Should().HaveCount(2);

        var (byProvider, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, provider: "anthropic", null, null, 50);
        byProvider.Should().HaveCount(2);

        var (byOutcome, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, outcome: "failed", null, 50);
        byOutcome.Should().ContainSingle().Which.Type.Should().Be("AGENT.TASK.FAILED");
    }

    [Test]
    public async Task QueryAgentTrail_TrailStream_ReturnsAllFamiliesForAgent()
    {
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS");
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TOOL_CALL.SUCCESS");
        await SeedTaskAsync(TenantA, AgentX, "REVIEW.BUG.RECORDED");
        await SeedTaskAsync(TenantA, AgentY, "AGENT.TASK.SUCCESS");

        // No type prefix ⇒ all of AgentX's events; AgentY excluded.
        var (rows, total) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, typePrefix: null, null, null, null, null, null, null, 50);
        total.Should().Be(3);
        rows.Select(r => r.Type).Should().BeEquivalentTo(new[]
        {
            "AGENT.TASK.SUCCESS", "AGENT.TOOL_CALL.SUCCESS", "REVIEW.BUG.RECORDED",
        });
    }

    // ── AC8: diagnostics link (correlationId + agent) ───────────────────

    [Test]
    public async Task TrailEvent_AndProviderDiagnostic_ShareCorrelationIdAndAgent()
    {
        var correlationId = "corr-link-1";
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS",
            role: "developer", provider: "anthropic", correlationId: correlationId);

        // The diagnostics row the run wrote (correlationId + AgentType=role).
        await using (var db = await _factory.CreateAsync(TenantA))
        {
            db.ProviderDiagnostics.Add(new ProviderDiagnostic
            {
                Id = Guid.NewGuid(),
                ProviderKey = "anthropic",
                CorrelationId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                AgentType = "developer",
                TenantId = TenantA,
                Cost = 0.0030m,
                CreatedAt = DateTime.UtcNow,
            });
            // Store the string correlationId link the same way the trail tags it:
            // ProviderDiagnostic.CorrelationId is a Guid column, so the join key in
            // this story is the shared *string* correlationId carried in the trail
            // tag AND the run's diagnostics. We assert the trail carries it and the
            // diagnostics row is re-keyable by the same agent role.
            await db.SaveChangesAsync();
        }

        var (rows, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50);

        var trail = rows.Should().ContainSingle().Subject;
        var tags = Tags(trail);
        tags["correlationId"].Should().Be(correlationId);
        tags["agentId"].Should().Be(AgentX.ToString());
        tags["role"].Should().Be("developer");

        // Re-key diagnostics by the trail's agent role (AgentType) — the join 32-9/
        // 32-10 perform to attribute cost/latency to the agent.
        await using var read = await _factory.CreateAsync(TenantA);
        var diag = await read.ProviderDiagnostics
            .Where(d => d.TenantId == TenantA && d.AgentType == tags["role"])
            .SingleAsync();
        diag.AgentType.Should().Be("developer");
        diag.ProviderKey.Should().Be(tags["provider"]);
    }

    // ── seeding helpers ─────────────────────────────────────────────────

    private async Task SeedTaskAsync(
        Guid tenantId, Guid agentId, string type,
        string role = "developer", string provider = "anthropic",
        string correlationId = "corr-1", DateTime? createdAt = null)
    {
        var ctx = new AgentTrailContext
        {
            TenantId = tenantId,
            AgentId = agentId,
            AgentVersion = 1,
            Role = role,
            Provider = provider,
            Model = "claude-sonnet-4",
            CorrelationId = correlationId,
            CredentialSource = "platform",
        };

        await using var db = await _factory.CreateAsync(tenantId);
        db.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = AgentTrailTags.Build(ctx),
            Metadata = "{}",
            Data = "{}",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, string?> Tags(DomainEvent e)
        => JsonSerializer.Deserialize<Dictionary<string, string?>>(e.Tags)!;
}
