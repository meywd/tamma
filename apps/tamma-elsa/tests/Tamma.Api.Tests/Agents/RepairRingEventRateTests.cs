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
/// Story 39-9 (AC7) — proves that per-<c>(role, action) × documentType</c> repair
/// rates are computable FROM THE EVENTS ALONE via the Story 4-7 query path
/// (<see cref="IEventRepository.QueryEventsAsync"/> with the <c>"LLM."</c> type
/// prefix), with no extra store. Seeds a mixed <c>LLM.*</c> stream across two cells
/// through the real <see cref="EventRepository"/> and recomputes validation-failure,
/// first-repair-success, and exhaustion counts purely by tag grouping.
///
/// <para><b>Docker-gated:</b> runs against a Postgres testcontainer (the JSONB tag
/// predicates + BIGSERIAL cursor are not translatable on EF-InMemory). Compiles
/// everywhere; its runtime is CI's Postgres.</para>
/// </summary>
[TestFixture]
public class RepairRingEventRateTests
{
    private static readonly Guid Tenant = Guid.Parse("eeeeeeee-9999-9999-9999-eeeeeeeeeeee");

    private PostgreSqlContainer _postgres = null!;
    private DbContextOptions<TenantDbContext> _options = null!;
    private ITenantDbContextFactory _factory = null!;
    private EventRepository _repo = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("repair_rate_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();

        _options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using (var db = new TestTenantDbContext(_options, Tenant))
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
        cmd.CommandText = "TRUNCATE TABLE domain_events;";
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task PerCellRates_ComputableFromEventsAlone()
    {
        // Cell 1 — developer / issue-decomposition / decomposition:
        //   3 first-attempt validation failures; 2 repaired; 1 exhausted.
        await SeedValidationFailedAsync("developer", "issue-decomposition", "decomposition", repairTurn: 0);
        await SeedValidationFailedAsync("developer", "issue-decomposition", "decomposition", repairTurn: 0);
        await SeedValidationFailedAsync("developer", "issue-decomposition", "decomposition", repairTurn: 0);
        await SeedRepairSucceededAsync("developer", "issue-decomposition", "decomposition");
        await SeedRepairSucceededAsync("developer", "issue-decomposition", "decomposition");
        await SeedRepairExhaustedAsync("developer", "issue-decomposition", "decomposition");

        // Cell 2 — architect / design-proposal / design:
        //   2 first-attempt validation failures; 0 repaired; 2 exhausted.
        await SeedValidationFailedAsync("architect", "design-proposal", "design", repairTurn: 0);
        await SeedValidationFailedAsync("architect", "design-proposal", "design", repairTurn: 0);
        await SeedRepairExhaustedAsync("architect", "design-proposal", "design");
        await SeedRepairExhaustedAsync("architect", "design-proposal", "design");

        // Read the whole LLM.* stream via the Story 4-7 query path.
        var (events, _) = await _repo.QueryEventsAsync(
            Tenant, type: "LLM.", typeIsPrefix: true,
            correlationId: null, actor: null, from: null, to: null,
            cursor: null, limit: 500);

        // Group by the (role, action, documentType) cell — tags alone.
        var byCell = events
            .GroupBy(e => (
                Role: TagOf(e, "role"),
                Action: TagOf(e, "action"),
                DocumentType: TagOf(e, "documentType")))
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Cell 1 ──
        var cell1 = byCell[("developer", "issue-decomposition", "decomposition")];
        FirstAttemptFailures(cell1).Should().Be(3);
        Count(cell1, RepairRingEventTypes.RepairSucceeded).Should().Be(2);
        Count(cell1, RepairRingEventTypes.RepairExhausted).Should().Be(1);
        FirstRepairSuccessRate(cell1).Should().BeApproximately(2d / 3d, 1e-9);
        ExhaustionRate(cell1).Should().BeApproximately(1d / 3d, 1e-9);

        // ── Cell 2 ──
        var cell2 = byCell[("architect", "design-proposal", "design")];
        FirstAttemptFailures(cell2).Should().Be(2);
        Count(cell2, RepairRingEventTypes.RepairSucceeded).Should().Be(0);
        Count(cell2, RepairRingEventTypes.RepairExhausted).Should().Be(2);
        FirstRepairSuccessRate(cell2).Should().Be(0d);
        ExhaustionRate(cell2).Should().Be(1d);
    }

    // ── rate helpers (events → rates, tags only) ─────────────────────────

    private static int FirstAttemptFailures(IEnumerable<DomainEvent> cell) =>
        cell.Count(e => e.Type == RepairRingEventTypes.ValidationFailed && TagOf(e, "repairTurn") == "0");

    private static int Count(IEnumerable<DomainEvent> cell, string type) =>
        cell.Count(e => e.Type == type);

    private static double FirstRepairSuccessRate(IReadOnlyList<DomainEvent> cell)
    {
        var failures = FirstAttemptFailures(cell);
        return failures == 0 ? 0d : (double)Count(cell, RepairRingEventTypes.RepairSucceeded) / failures;
    }

    private static double ExhaustionRate(IReadOnlyList<DomainEvent> cell)
    {
        var failures = FirstAttemptFailures(cell);
        return failures == 0 ? 0d : (double)Count(cell, RepairRingEventTypes.RepairExhausted) / failures;
    }

    // ── seed helpers ─────────────────────────────────────────────────────

    private Task SeedValidationFailedAsync(string role, string action, string documentType, int repairTurn) =>
        SeedAsync(RepairRingEventTypes.ValidationFailed, role, action, documentType, repairTurn);

    private Task SeedRepairSucceededAsync(string role, string action, string documentType) =>
        SeedAsync(RepairRingEventTypes.RepairSucceeded, role, action, documentType, repairTurn: 1);

    private Task SeedRepairExhaustedAsync(string role, string action, string documentType) =>
        SeedAsync(RepairRingEventTypes.RepairExhausted, role, action, documentType, repairTurn: 1);

    private async Task SeedAsync(string type, string role, string action, string documentType, int repairTurn)
    {
        var tags = JsonSerializer.Serialize(new
        {
            issueId = "ISSUE-1",
            documentType,
            role,
            action,
            repairTurn,
            correlationId = "corr-1",
            tenantId = Tenant.ToString(),
        });

        await using var db = await _factory.CreateAsync(Tenant);
        db.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = Tenant,
            Tags = tags,
            Metadata = "{}",
            Data = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string? TagOf(DomainEvent e, string key)
    {
        if (string.IsNullOrEmpty(e.Tags))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(e.Tags);
        if (!doc.RootElement.TryGetProperty(key, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => prop.GetRawText(),
        };
    }
}
