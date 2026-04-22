using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data;
using Tamma.Data.Entities;
using BudgetConfig = Tamma.Api.Services.Diagnostics.Models.BudgetConfig;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Integration tests against the minimal API diagnostics endpoints through
/// the real Postgres-backed test fixture. Tenant isolation is handled via
/// the dev-mode <c>AllowAnonymous</c> auth policy (configured in
/// <see cref="ApiTestFixture"/>).
/// </summary>
[TestFixture]
public class DiagnosticsEndpointsIntegrationTests
{
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        await DiagnosticsSetUpFixture.ResetDatabaseAsync();
        _client = DiagnosticsTestHarness.CreateClient();
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // Ingest
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task IngestDiagnostic_PersistsRowAndKeepsCacheWarm()
    {
        var body = new
        {
            providerKey = "anthropic-claude",
            durationMs = 250.0,
            tokensUsed = 1500,
            cost = 0.03m,
            model = "claude-sonnet-4.5",
            success = true,
            error = (string?)null
        };
        var resp = await _client.PostAsJsonAsync("/api/providers/diagnostics", body);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Verify via query endpoint
        var queryResp = await _client.GetAsync("/api/providers/diagnostics/query?providerKey=anthropic-claude");
        queryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await queryResp.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("total").GetInt32().Should().Be(1);

        // Verify the recent-events cache was populated (tenant-scoped).
        using var scope = DiagnosticsTestHarness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDiagnosticsService>();
        var recent = service.GetRecentEvents(tenantId: null, limit: 10);
        recent.Should().NotBeEmpty();
        recent.Should().OnlyContain(r => r.ProviderKey == "anthropic-claude");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Query filters
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task QueryDiagnostics_FiltersByProviderAndSuccess()
    {
        using (var scope = DiagnosticsTestHarness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.ProviderDiagnostics.AddRange(
                Row("p1", cost: 1m, success: true),
                Row("p1", cost: 1m, success: false),
                Row("p2", cost: 1m, success: true));
            await db.SaveChangesAsync();
        }

        var r1 = await _client.GetAsync("/api/providers/diagnostics/query?providerKey=p1");
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var p1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
        p1.GetProperty("total").GetInt32().Should().Be(2);

        var r2 = await _client.GetAsync("/api/providers/diagnostics/query?providerKey=p1&success=true");
        var p2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        p2.GetProperty("total").GetInt32().Should().Be(1);

        var r3 = await _client.GetAsync("/api/providers/diagnostics/query?success=false");
        var p3 = await r3.Content.ReadFromJsonAsync<JsonElement>();
        p3.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task QueryDiagnostics_HonoursDateRange()
    {
        var now = DateTime.UtcNow;
        using (var scope = DiagnosticsTestHarness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.ProviderDiagnostics.AddRange(
                Row("x", success: true, createdAt: now.AddHours(-5)),
                Row("x", success: true, createdAt: now.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddMinutes(1).ToString("O"));

        var r = await _client.GetAsync($"/api/providers/diagnostics/query?from={from}&to={to}");
        var payload = await r.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("total").GetInt32().Should().Be(1);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Report
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetReport_FiveMinuteBuckets_ReturnsAggregates()
    {
        var baseTime = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

        using (var scope = DiagnosticsTestHarness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.ProviderDiagnostics.AddRange(
                Row("p", cost: 1m, success: true, createdAt: baseTime.AddMinutes(1)),
                Row("p", cost: 2m, success: true, createdAt: baseTime.AddMinutes(2)),
                Row("p", cost: 4m, success: false, createdAt: baseTime.AddMinutes(6)));
            await db.SaveChangesAsync();
        }

        var fromStr = Uri.EscapeDataString(baseTime.ToString("O"));
        var toStr = Uri.EscapeDataString(baseTime.AddMinutes(15).ToString("O"));

        var r = await _client.GetAsync(
            $"/api/providers/diagnostics/report?from={fromStr}&to={toStr}&bucketSize=FiveMinutes");
        r.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await r.Content.ReadFromJsonAsync<JsonElement>();
        report.GetProperty("totalCalls").GetInt64().Should().Be(3);
        report.GetProperty("totalCost").GetDecimal().Should().Be(7m);
        report.GetProperty("buckets").GetArrayLength().Should().Be(2);
    }

    [Test]
    public async Task GetReport_EmptyRange_ReturnsZeroes()
    {
        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fromStr = Uri.EscapeDataString(from.ToString("O"));
        var toStr = Uri.EscapeDataString(from.AddHours(1).ToString("O"));

        var r = await _client.GetAsync(
            $"/api/providers/diagnostics/report?from={fromStr}&to={toStr}&bucketSize=Hour");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await r.Content.ReadFromJsonAsync<JsonElement>();
        report.GetProperty("totalCalls").GetInt64().Should().Be(0);
        report.GetProperty("totalCost").GetDecimal().Should().Be(0m);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Budget
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetBudget_ReturnsRealBudgetShape()
    {
        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var scope = DiagnosticsTestHarness.CreateScope())
        {
            var provider = scope.ServiceProvider.GetRequiredService<IBudgetConfigProvider>();
            provider.SetConfig(tenant, new BudgetConfig(
                LimitUsd: 100m,
                AlertThreshold: 0.8,
                PeriodStart: now.AddDays(-5),
                PeriodEnd: now.AddDays(5)));

            var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
            db.ProviderDiagnostics.Add(Row("p", cost: 30m, success: true, tenantId: tenant, createdAt: now.AddHours(-1)));
            await db.SaveChangesAsync();
        }

        var r = await _client.GetAsync($"/api/providers/diagnostics/budget/{tenant}");
        r.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("accountId").GetString().Should().Be(tenant.ToString());
        json.GetProperty("spent").GetDecimal().Should().Be(30m);
        json.GetProperty("limit").GetDecimal().Should().Be(100m);
        json.GetProperty("remaining").GetDecimal().Should().Be(70m);
        json.GetProperty("shouldAlert").GetBoolean().Should().BeFalse();
        json.GetProperty("isOverBudget").GetBoolean().Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static ProviderDiagnostic Row(
        string providerKey,
        decimal cost = 0m,
        bool success = true,
        Guid? tenantId = null,
        DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ProviderKey = providerKey,
        Cost = cost,
        Success = success,
        TenantId = tenantId,
        CreatedAt = DateTime.SpecifyKind(createdAt ?? DateTime.UtcNow, DateTimeKind.Utc)
    };
}
