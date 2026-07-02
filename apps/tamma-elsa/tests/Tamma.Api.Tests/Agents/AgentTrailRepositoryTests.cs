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
///   <item>opt-in total (review I2): <c>includeTotal=false</c> (default) skips the
///     unbounded <c>COUNT(*)</c> and returns a <c>null</c> total; <c>true</c>
///     computes it;</item>
///   <item>diagnostics re-key (honest scope): the trail is keyed by
///     <c>agentId</c> + <c>correlationId</c> WITHIN the tenant DCB stream. There is
///     NO per-run join to <see cref="ProviderDiagnostic"/> today — diagnostics carry
///     a <c>Guid?</c> correlationId and no <c>agentId</c>, and managed runs emit no
///     diagnostic row — so the only field the two share is the agent role
///     (<c>AgentType</c>), a role-scoped (not run-scoped) re-key. A true per-run
///     correlation is deferred (Story 35-2 diagnostics work).</item>
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
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50, includeTotal: true);
        var (bRows, bTotal) = await _repo.QueryAgentTrailAsync(
            TenantB, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50, includeTotal: true);

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
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50, includeTotal: true);

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

        // Page 1 (limit 2, DESC by SequenceNumber). Opt into the total here.
        var (p1, total) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, cursor: null, 2, includeTotal: true);
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
            TenantA, AgentX, typePrefix: null, null, null, null, null, null, null, 50, includeTotal: true);
        total.Should().Be(3);
        rows.Select(r => r.Type).Should().BeEquivalentTo(new[]
        {
            "AGENT.TASK.SUCCESS", "AGENT.TOOL_CALL.SUCCESS", "REVIEW.BUG.RECORDED",
        });
    }

    // ── diagnostics re-key: honest scope (role-only; NO per-run join today) ──
    //
    // This replaces an earlier test that claimed a "(correlationId, agentId)" join
    // to ProviderDiagnostic. That join is NOT executable against the current schema
    // (ProviderDiagnostic.CorrelationId is a Guid?, not the trail's string; there is
    // no agentId column, only AgentType = role; managed runs emit no diagnostic row).
    // The test now asserts ONLY what is real: the trail is keyed by agentId +
    // correlationId within the DCB stream, and the sole field it shares with a
    // diagnostic is the agent ROLE (a role-scoped, not run-scoped, re-key).

    [Test]
    public async Task TrailEvent_KeyedByAgentIdAndCorrelationId_DiagnosticsReKeyByRoleOnly()
    {
        // The trail event carries a STRING correlationId + agentId + role — the keys
        // a per-agent rollup uses WITHIN this tenant's DCB stream.
        var correlationId = "corr-link-1";
        await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS",
            role: "developer", provider: "anthropic", correlationId: correlationId);

        // A ProviderDiagnostic row for the same tenant + role. It is inserted with a
        // deliberately UNRELATED Guid correlationId to make the schema gap explicit:
        // ProviderDiagnostic.CorrelationId is a Guid? (never the trail's string tag)
        // and there is NO agentId column — only AgentType (= the role). A real managed
        // run would not even write one of these (it meters via IUsageEmitter); this row
        // exists purely to prove the ONLY field the two share is the role.
        var diagCorrelation = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await using (var db = await _factory.CreateAsync(TenantA))
        {
            db.ProviderDiagnostics.Add(new ProviderDiagnostic
            {
                Id = Guid.NewGuid(),
                ProviderKey = "anthropic",
                CorrelationId = diagCorrelation,
                AgentType = "developer",
                TenantId = TenantA,
                Cost = 0.0030m,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var (rows, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, null, 50);

        var trail = rows.Should().ContainSingle().Subject;
        var tags = Tags(trail);
        // Achievable trail keying: agentId + correlationId (both string tags) + role.
        tags["correlationId"].Should().Be(correlationId);
        tags["agentId"].Should().Be(AgentX.ToString());
        tags["role"].Should().Be("developer");

        await using var read = await _factory.CreateAsync(TenantA);
        var diag = await read.ProviderDiagnostics
            .Where(d => d.TenantId == TenantA && d.AgentType == tags["role"])
            .SingleAsync();

        // The ONLY shared re-key today is the agent ROLE — role-scoped, not run-scoped.
        diag.AgentType.Should().Be(tags["role"]);
        diag.ProviderKey.Should().Be(tags["provider"]);

        // And a per-RUN join is genuinely NOT expressible: the diagnostic's
        // correlationId is a Guid unrelated to the trail's string correlationId, and
        // the diagnostic carries no agentId at all.
        diag.CorrelationId.Should().Be(diagCorrelation);
        diag.CorrelationId.ToString().Should().NotBe(tags["correlationId"]);
    }

    // ── opt-in total (review I2): includeTotal skips/computes COUNT(*) ────

    [Test]
    public async Task QueryAgentTrail_IncludeTotalFalse_SkipsCount_ReturnsNullTotal_StillPagesByCursor()
    {
        for (var i = 0; i < 3; i++)
        {
            await SeedTaskAsync(TenantA, AgentX, "AGENT.TASK.SUCCESS");
        }

        // Default (includeTotal:false) ⇒ no COUNT(*); Total is null ("not computed").
        var (rowsNoTotal, noTotal) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, cursor: null, 2);
        noTotal.Should().BeNull();
        rowsNoTotal.Should().HaveCount(2, "the page still fills; pagination uses the cursor, not the total");

        // Cursor still advances without a total.
        var (page2, _) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null,
            cursor: rowsNoTotal[^1].SequenceNumber, 2);
        page2.Should().ContainSingle();

        // Opt in ⇒ the exact count is computed.
        var (_, withTotal) = await _repo.QueryAgentTrailAsync(
            TenantA, AgentX, "AGENT.TASK", null, null, null, null, null, cursor: null, 2, includeTotal: true);
        withTotal.Should().Be(3);
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
