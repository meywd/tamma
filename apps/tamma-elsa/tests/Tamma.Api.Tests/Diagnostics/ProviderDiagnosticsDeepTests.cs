using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Story 23-6 — tests for the deep provider-diagnostics aggregation
/// (<see cref="IDiagnosticsService.GetDeepReportAsync"/>) and its
/// <c>/api/providers/diagnostics/deep</c> endpoint. Covers latency percentiles,
/// error classification, per-model usage, tenant isolation, and the
/// no-platform-margin-leak invariant (Story 34-5 rule).
/// </summary>
[TestFixture]
public class ProviderDiagnosticsDeepTests
{
    private IServiceScope _scope = null!;
    private IDiagnosticsService _service = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public async Task SetUp()
    {
        await DiagnosticsSetUpFixture.ResetDatabaseAsync();
        _scope = DiagnosticsTestHarness.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _service = _scope.ServiceProvider.GetRequiredService<IDiagnosticsService>();
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // Percentiles + error classification + per-model usage
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDeepReportAsync_ComputesPercentilesErrorsAndModels()
    {
        var tenant = Guid.NewGuid();
        var t = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        // p1 / m1 — 4 calls, one failure (rate_limit). Durations 100/200/300/400.
        await SeedAsync(tenant, t.AddMinutes(1), "p1", model: "m1", durationMs: 100, success: true, tokensUsed: 10, cost: 1m);
        await SeedAsync(tenant, t.AddMinutes(2), "p1", model: "m1", durationMs: 200, success: true, tokensUsed: 20, cost: 2m);
        await SeedAsync(tenant, t.AddMinutes(3), "p1", model: "m1", durationMs: 300, success: true, tokensUsed: 30, cost: 3m);
        await SeedAsync(tenant, t.AddMinutes(4), "p1", model: "m1", durationMs: 400, success: false, errorCode: "rate_limit", tokensUsed: 0, cost: 0m);

        // p2 / m2 — 2 calls, one failure with no structured code (→ "unknown").
        await SeedAsync(tenant, t.AddMinutes(5), "p2", model: "m2", durationMs: 50, success: true, tokensUsed: 5, cost: 0.5m);
        await SeedAsync(tenant, t.AddMinutes(6), "p2", model: "m2", durationMs: 60, success: false, errorCode: null, tokensUsed: 0, cost: 0m);

        var report = await _service.GetDeepReportAsync(tenant, t, t.AddMinutes(30), providerKey: null);

        report.Providers.Should().HaveCount(2);
        report.TotalCalls.Should().Be(6);
        report.TotalErrors.Should().Be(2);
        report.TotalTokens.Should().Be(65);
        report.TotalCost.Should().Be(6.5m);

        // Busiest provider first.
        var p1 = report.Providers[0];
        p1.ProviderKey.Should().Be("p1");
        p1.TotalCalls.Should().Be(4);
        p1.SuccessCount.Should().Be(3);
        p1.FailureCount.Should().Be(1);
        p1.SuccessRate.Should().BeApproximately(0.75, 0.0001);
        p1.ErrorRate.Should().BeApproximately(0.25, 0.0001);

        // Nearest-rank over [100,200,300,400].
        p1.Latency.P50.Should().Be(200);
        p1.Latency.P95.Should().Be(400);
        p1.Latency.P99.Should().Be(400);
        p1.Latency.Max.Should().Be(400);
        p1.Latency.Avg.Should().BeApproximately(250.0, 0.0001);

        p1.TotalTokens.Should().Be(60);
        p1.TotalCost.Should().Be(6m);

        p1.Errors.Should().ContainSingle();
        p1.Errors[0].ErrorClass.Should().Be("rate_limit");
        p1.Errors[0].Count.Should().Be(1);
        p1.Errors[0].Share.Should().BeApproximately(1.0, 0.0001);

        p1.Models.Should().ContainSingle();
        p1.Models[0].Model.Should().Be("m1");
        p1.Models[0].TotalCalls.Should().Be(4);
        p1.Models[0].TotalCost.Should().Be(6m);

        var p2 = report.Providers[1];
        p2.ProviderKey.Should().Be("p2");
        p2.Errors.Should().ContainSingle(e => e.ErrorClass == "unknown" && e.Count == 1);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Tenant isolation — a tenant only sees its own providers
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDeepReportAsync_IsolatesByTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var t = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        await SeedAsync(tenantA, t.AddMinutes(1), "provider-a", model: "ma", durationMs: 100, success: true, tokensUsed: 10, cost: 5m);
        await SeedAsync(tenantB, t.AddMinutes(1), "provider-b", model: "mb", durationMs: 100, success: true, tokensUsed: 99, cost: 999m);

        var reportA = await _service.GetDeepReportAsync(tenantA, t, t.AddMinutes(30), providerKey: null);

        reportA.Providers.Should().ContainSingle();
        reportA.Providers[0].ProviderKey.Should().Be("provider-a");
        reportA.TotalCost.Should().Be(5m);
        reportA.Providers.Should().NotContain(p => p.ProviderKey == "provider-b");
    }

    [Test]
    public async Task GetDeepReportAsync_ProviderKeyFilter_NarrowsToOneProvider()
    {
        var tenant = Guid.NewGuid();
        var t = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        await SeedAsync(tenant, t.AddMinutes(1), "p1", model: "m1", durationMs: 100, success: true);
        await SeedAsync(tenant, t.AddMinutes(2), "p2", model: "m2", durationMs: 100, success: true);

        var report = await _service.GetDeepReportAsync(tenant, t, t.AddMinutes(30), providerKey: "p1");

        report.Providers.Should().ContainSingle();
        report.Providers[0].ProviderKey.Should().Be("p1");
    }

    [Test]
    public async Task GetDeepReportAsync_EmptyRange_ReturnsEmpty()
    {
        var tenant = Guid.NewGuid();
        await EnsureTenantAsync(tenant);
        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var report = await _service.GetDeepReportAsync(tenant, from, from.AddHours(1), providerKey: null);

        report.Providers.Should().BeEmpty();
        report.TotalCalls.Should().Be(0);
        report.TotalCost.Should().Be(0m);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Endpoint shape + no-platform-margin leak (Story 34-5 rule)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDeepDiagnostics_Endpoint_ReturnsShape_AndDoesNotLeakPlatformMargin()
    {
        var tenant = Guid.NewGuid();
        var t = DateTime.UtcNow;
        await SeedAsync(tenant, t.AddMinutes(-5), "anthropic-claude", model: "claude-sonnet-4",
            durationMs: 250, success: true, tokensUsed: 1500, cost: 0.03m);

        using var client = DiagnosticsTestHarness.CreateClient();
        // Tenant-scoped route — present the seeded tenant so the fail-closed
        // guard passes and the concrete-tenant path returns only its data.
        client.DefaultRequestHeaders.Add(DiagnosticsTestHarness.TenantHeader, tenant.ToString());
        var fromStr = Uri.EscapeDataString(t.AddHours(-1).ToString("O"));
        var toStr = Uri.EscapeDataString(t.AddMinutes(1).ToString("O"));

        var resp = await client.GetAsync(
            $"/api/providers/diagnostics/deep?from={fromStr}&to={toStr}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await resp.Content.ReadAsStringAsync();

        // Tenant's OWN spend is allowed (same Cost column already shipped by
        // /diagnostics/report). Platform-internal economics must NEVER appear.
        raw.Should().Contain("totalCost");
        raw.ToLowerInvariant().Should().NotContain("margin");
        raw.ToLowerInvariant().Should().NotContain("markup");
        raw.ToLowerInvariant().Should().NotContain("costbasis");
        raw.ToLowerInvariant().Should().NotContain("sellprice");

        var json = JsonSerializer.Deserialize<JsonElement>(raw);
        json.GetProperty("providers").GetArrayLength().Should().Be(1);
        var provider = json.GetProperty("providers")[0];
        provider.GetProperty("providerKey").GetString().Should().Be("anthropic-claude");
        provider.GetProperty("latency").GetProperty("p50").GetDouble().Should().Be(250);
        provider.GetProperty("models").GetArrayLength().Should().Be(1);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private async Task SeedAsync(
        Guid tenantId,
        DateTime createdAt,
        string providerKey,
        string? model = null,
        double durationMs = 0,
        bool success = true,
        string? errorCode = null,
        int tokensUsed = 0,
        decimal cost = 0m)
    {
        await EnsureTenantAsync(tenantId);

        var factory = _scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(tenantId);

        tdb.ProviderDiagnostics.Add(new ProviderDiagnostic
        {
            Id = Guid.NewGuid(),
            ProviderKey = providerKey,
            TenantId = tenantId,
            Model = model,
            RequestDurationMs = durationMs,
            Success = success,
            ErrorCode = errorCode,
            ErrorMessage = success ? null : (errorCode ?? "error"),
            TokensUsed = tokensUsed,
            InputTokens = tokensUsed,
            OutputTokens = 0,
            Cost = cost,
            CreatedAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
        });
        await tdb.SaveChangesAsync();
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        if (await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
            return;
        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = $"Test {tenantId:N}",
            Slug = $"t-{tenantId:N}",
            Plan = "free",
        });
        await _db.SaveChangesAsync();
        await DiagnosticsSetUpFixture.ProvisionTenantAsync(tenantId);
    }
}
