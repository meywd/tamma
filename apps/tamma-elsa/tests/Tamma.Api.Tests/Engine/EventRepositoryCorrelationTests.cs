using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Engine;

/// <summary>
/// Read-endpoint hardening for <see cref="EventRepository"/>'s correlation-id reads
/// (backing Story 4-8 replay + Story 21-4 run-detail), against the fixture's tenant
/// Postgres so the JSONB lookup + BIGSERIAL sequence exercise the real EF/Postgres path.
///
/// <para>Covers:
/// <list type="bullet">
///   <item><b>Fix C</b> — the BOUNDED overload caps the fetch and SIGNALS truncation
///     (<c>Truncated == true</c>) rather than materialising an unbounded run or silently
///     dropping the tail.</item>
///   <item><b>Fix D</b> — BOTH overloads throw <see cref="NotSupportedException"/> on
///     <see cref="Guid.Empty"/> (parity with QueryEventsAsync / QueryAgentTrailAsync — a
///     null/empty tenant must never reach a real query).</item>
/// </list></para>
/// </summary>
[TestFixture]
public class EventRepositoryCorrelationTests
{
    private IServiceScope _scope = null!;
    private IEventRepository _events = null!;
    private ITenantDbContextFactory _factory = null!;

    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _factory = _scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── Fix C: bounded fetch caps + signals truncation ────────────────────────

    [Test]
    public async Task BoundedList_OverCap_ReturnsCappedSlice_Truncated()
    {
        var tenantId = Guid.NewGuid();
        await SeedRunAsync(tenantId, "run-big", new[] { "E.1", "E.2", "E.3", "E.4", "E.5" });

        var (events, truncated) = await _events.ListByCorrelationIdAsync(tenantId, "run-big", maxEvents: 2);

        truncated.Should().BeTrue("the run (5 events) exceeds the cap of 2");
        events.Count.Should().Be(2, "the capped slice returns exactly maxEvents");
        // Oldest-first: the cap keeps the FIRST two by sequence number.
        events.Select(e => e.Type).Should().ContainInOrder("E.1", "E.2");
    }

    [Test]
    public async Task BoundedList_AtOrUnderCap_ReturnsAll_NotTruncated()
    {
        var tenantId = Guid.NewGuid();
        await SeedRunAsync(tenantId, "run-fits", new[] { "E.1", "E.2", "E.3" });

        var (events, truncated) = await _events.ListByCorrelationIdAsync(tenantId, "run-fits", maxEvents: 3);

        truncated.Should().BeFalse("3 events == the cap of 3 is not truncated");
        events.Count.Should().Be(3);
        events.Select(e => e.Type).Should().ContainInOrder("E.1", "E.2", "E.3");
    }

    // ── Fix D: empty-tenant guard on BOTH overloads ───────────────────────────

    [Test]
    public void List_EmptyTenant_Throws()
    {
        var act = async () => await _events.ListByCorrelationIdAsync(Guid.Empty, "any");
        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public void BoundedList_EmptyTenant_Throws()
    {
        var act = async () => await _events.ListByCorrelationIdAsync(Guid.Empty, "any", maxEvents: 10);
        act.Should().ThrowAsync<NotSupportedException>();
    }

    // ── helpers (mirror ReplayEndpointTests seeding) ──────────────────────────

    private async Task EnsureTenantProvisionedAsync(Guid tenantId)
    {
        var cp = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        if (!await cp.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            cp.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = $"Test {tenantId:N}",
                Slug = $"t-{tenantId:N}",
                Plan = "free"
            });
            await cp.SaveChangesAsync();
        }
        await ApiTestFixture.ProvisionTenantAsync(tenantId);
    }

    /// <summary>
    /// Insert a run's events one-by-one (so BIGSERIAL sequence tracks insertion order)
    /// with the correlationId stamped into Tags — the shape the correlation-id reads
    /// filter on.
    /// </summary>
    private async Task SeedRunAsync(Guid tenantId, string correlationId, string[] types)
    {
        await EnsureTenantProvisionedAsync(tenantId);

        var tags = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["correlationId"] = correlationId,
        });

        var i = 0;
        foreach (var type in types)
        {
            await using var db = await _factory.CreateAsync(tenantId);
            db.DomainEvents.Add(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                TenantId = tenantId,
                Tags = tags,
                Metadata = "{\"workflowVersion\":\"1.0.0\",\"eventSource\":\"system\"}",
                Data = "{}",
                CreatedAt = Base.AddSeconds(i),
            });
            await db.SaveChangesAsync();
            i++;
        }
    }
}
