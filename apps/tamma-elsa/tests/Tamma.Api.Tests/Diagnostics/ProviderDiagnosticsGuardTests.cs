using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Diagnostics;
using Tamma.Api.Services.Diagnostics.Models;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Diagnostics;

/// <summary>
/// Story 23-6 review — guards on the tenant-scoped (<c>SettingsView</c>)
/// diagnostics read endpoints:
/// <list type="bullet">
///   <item><b>Fix 1</b> — a null <see cref="ITenantContext.TenantId"/> FAILS
///     CLOSED (404 <c>no_active_tenant</c>) instead of fanning out over every
///     tenant's economics. Applies to <c>/diagnostics/deep</c> and the
///     siblings <c>/diagnostics/report</c>, <c>/diagnostics/query</c>,
///     <c>/diagnostics</c>.</item>
///   <item><b>Fix 2</b> — an over-wide <c>[from,to)</c> window is rejected
///     (400 <c>window_too_large</c>) before the repository materializes the
///     whole range in memory.</item>
///   <item><b>Fix 3</b> — a client offset (e.g. <c>+02:00</c>) is CONVERTED to
///     UTC, not relabeled, so the query window is correct regardless of host
///     TZ (TZ-independent — the offset is explicit in the request string).</item>
/// </list>
/// The guards live in the endpoint handlers, so these tests call the static
/// handlers directly with a fake <see cref="ITenantContext"/> — the cleanest
/// way to inject a null / concrete tenant without an HTTP round-trip.
/// </summary>
[TestFixture]
public class ProviderDiagnosticsGuardTests
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
    // Fix 1 — fail closed on null tenant (no cross-tenant economics fan-out)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDeepDiagnostics_NullTenant_FailsClosed_WithoutCallingService()
    {
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.GetDeepDiagnostics(
            svc, new FakeTenantContext(null), from: null, to: null, providerKey: null);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        svc.Called.Should().BeFalse("the guard must reject before any cross-tenant fan-out");
    }

    [Test]
    public async Task GetReport_NullTenant_FailsClosed_WithoutCallingService()
    {
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.GetReport(
            svc, new FakeTenantContext(null), from: null, to: null, bucketSize: null, groupBy: null);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        svc.Called.Should().BeFalse();
    }

    [Test]
    public async Task GetReport_NullTenant_WithGroupBy_FailsClosed()
    {
        // The groupBy path routes to GetDimensionReportAsync (which throws
        // NotSupportedException on a null tenant → 500). The fail-closed guard
        // must short-circuit to a clean 404 before that.
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.GetReport(
            svc, new FakeTenantContext(null), from: null, to: null, bucketSize: null, groupBy: "provider");

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        svc.Called.Should().BeFalse();
    }

    [Test]
    public async Task QueryDiagnostics_NullTenant_FailsClosed_WithoutCallingService()
    {
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.QueryDiagnostics(
            svc, new FakeTenantContext(null), providerKey: null, from: null, to: null,
            limit: null, offset: null, success: null, model: null);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        svc.Called.Should().BeFalse();
    }

    [Test]
    public async Task GetDiagnostics_NullTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new ThrowingDiagnosticsRepository();

        var result = await ProviderEndpoints.GetDiagnostics(
            repo, new FakeTenantContext(null), limit: null, offset: null);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.Called.Should().BeFalse();
    }

    [Test]
    public async Task GetDeepDiagnostics_ConcreteTenant_ReturnsOnlyThatTenantsData()
    {
        // Concrete tenant still resolves — and only that tenant's data (the
        // existing isolation invariant stays green). Seed a foreign tenant that
        // must NOT appear.
        var tenant = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var t = new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(tenant, t.AddMinutes(1), "mine", cost: 5m);
        await SeedAsync(foreign, t.AddMinutes(1), "theirs", cost: 999m);

        var result = await ProviderEndpoints.GetDeepDiagnostics(
            _service, new FakeTenantContext(tenant),
            from: t.ToString("O"), to: t.AddMinutes(30).ToString("O"), providerKey: null);

        StatusOf(result).Should().Be(200);
        var report = ValueOf<ProviderDiagnosticsDeepReport>(result);
        report.Providers.Select(p => p.ProviderKey).Should().ContainSingle().Which.Should().Be("mine");
        report.TotalCost.Should().Be(5m);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Fix 2 — max-window clamp (DoS: unbounded in-memory materialization)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDeepDiagnostics_OverWideWindow_Returns400_WithoutCallingService()
    {
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.GetDeepDiagnostics(
            svc, new FakeTenantContext(Guid.NewGuid()),
            from: "2000-01-01T00:00:00Z", to: "2100-01-01T00:00:00Z", providerKey: null);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("window_too_large");
        svc.Called.Should().BeFalse("the clamp must reject before FetchDetailAsync materializes the table");
    }

    [Test]
    public async Task GetReport_OverWideWindow_Returns400_WithoutCallingService()
    {
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.GetReport(
            svc, new FakeTenantContext(Guid.NewGuid()),
            from: "2000-01-01T00:00:00Z", to: "2100-01-01T00:00:00Z", bucketSize: "day", groupBy: null);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("window_too_large");
        svc.Called.Should().BeFalse();
    }

    [Test]
    public async Task GetDeepDiagnostics_NinetyDayWindow_IsAllowed()
    {
        // Boundary — exactly 90 days must be allowed (clamp is strictly >90d).
        var tenant = Guid.NewGuid();
        await EnsureTenantAsync(tenant);

        var result = await ProviderEndpoints.GetDeepDiagnostics(
            _service, new FakeTenantContext(tenant),
            from: "2026-01-01T00:00:00Z", to: "2026-04-01T00:00:00Z", providerKey: null); // 90 days

        StatusOf(result).Should().Be(200);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Fix 3 — client offset CONVERTED to UTC, not relabeled (TZ-independent)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetDeepDiagnostics_FromWithPositiveOffset_ConvertsToUtc_NotRelabel()
    {
        var tenant = Guid.NewGuid();
        // Window: from = 12:00+02:00 == 10:00Z, to = 12:00Z ⇒ [10:00Z, 12:00Z).
        // 10:30Z is INSIDE; 09:30Z is OUTSIDE. A relabel would treat `from` as
        // 12:00Z, collapsing the window and dropping the 10:30Z row. The offset
        // is explicit in the string ⇒ the assertion is independent of host TZ.
        await SeedAsync(tenant, new DateTime(2026, 4, 16, 10, 30, 0, DateTimeKind.Utc), "p-in", cost: 1m);
        await SeedAsync(tenant, new DateTime(2026, 4, 16, 9, 30, 0, DateTimeKind.Utc), "p-before", cost: 1m);

        var result = await ProviderEndpoints.GetDeepDiagnostics(
            _service, new FakeTenantContext(tenant),
            from: "2026-04-16T12:00:00+02:00",
            to: "2026-04-16T12:00:00Z",
            providerKey: null);

        StatusOf(result).Should().Be(200);
        var report = ValueOf<ProviderDiagnosticsDeepReport>(result);
        var keys = report.Providers.Select(p => p.ProviderKey).ToList();
        keys.Should().Contain("p-in", "12:00+02:00 must convert to 10:00Z so the 10:30Z row is in-window");
        keys.Should().NotContain("p-before", "09:30Z is before the converted 10:00Z boundary");
    }

    [Test]
    public void TryParseUtcBoundary_ConvertsOffsetToUtc_HostIndependent()
    {
        // Deterministic, host-TZ-independent (the .NET binder converts offsets
        // on a UTC host, hiding a relabel — so we assert the parse directly).
        // +02:00 ⇒ CONVERT (12:00+02:00 == 10:00Z), Z ⇒ 0 offset, no-offset ⇒
        // assume UTC. All yield Kind=Utc. A relabel would keep the 12:00 wall
        // clock, shifting the window by two hours on every host.
        ProviderEndpoints.TryParseUtcBoundary("2026-04-16T12:00:00+02:00", out var withOffset).Should().BeTrue();
        withOffset.Kind.Should().Be(DateTimeKind.Utc);
        withOffset.Should().Be(new DateTime(2026, 4, 16, 10, 0, 0, DateTimeKind.Utc));

        ProviderEndpoints.TryParseUtcBoundary("2026-04-16T12:00:00Z", out var withZ).Should().BeTrue();
        withZ.Kind.Should().Be(DateTimeKind.Utc);
        withZ.Should().Be(new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc));

        ProviderEndpoints.TryParseUtcBoundary("2026-04-16T12:00:00", out var noOffset).Should().BeTrue();
        noOffset.Kind.Should().Be(DateTimeKind.Utc);
        noOffset.Should().Be(new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc));

        ProviderEndpoints.TryParseUtcBoundary("not-a-date", out _).Should().BeFalse();
    }

    [Test]
    public async Task QueryDiagnostics_InvalidFrom_Returns400()
    {
        var svc = new ThrowingDiagnosticsService();

        var result = await ProviderEndpoints.QueryDiagnostics(
            svc, new FakeTenantContext(Guid.NewGuid()), providerKey: null,
            from: "not-a-date", to: null, limit: null, offset: null, success: null, model: null);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("invalid_from");
        svc.Called.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static int? StatusOf(IResult result)
        => (result as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static T ValueOf<T>(IResult result)
        => (T)((IValueHttpResult)result).Value!;

    private async Task SeedAsync(Guid tenantId, DateTime createdAt, string providerKey, decimal cost = 0m)
    {
        await EnsureTenantAsync(tenantId);
        var factory = _scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        await using var tdb = await factory.CreateAsync(tenantId);
        tdb.ProviderDiagnostics.Add(new ProviderDiagnostic
        {
            Id = Guid.NewGuid(),
            ProviderKey = providerKey,
            TenantId = tenantId,
            Model = "m",
            RequestDurationMs = 100,
            Success = true,
            TokensUsed = 10,
            InputTokens = 10,
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

    // ── Test doubles ─────────────────────────────────────────────────────

    private sealed class FakeTenantContext(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    /// <summary>
    /// Every method throws — proves the endpoint guard short-circuits BEFORE
    /// reaching the (cross-tenant fan-out capable) service.
    /// </summary>
    private sealed class ThrowingDiagnosticsService : IDiagnosticsService
    {
        public bool Called { get; private set; }

        private T Fail<T>()
        {
            Called = true;
            throw new InvalidOperationException("service must not be called after a fail-closed guard");
        }

        public Task<Guid> RecordEventAsync(ProviderDiagnostic diag, CancellationToken ct = default) => Fail<Task<Guid>>();
        public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(DiagnosticsFilter filter, CancellationToken ct = default)
            => Fail<Task<(List<ProviderDiagnostic>, int)>>();
        public Task<DiagnosticsReport> GetReportAsync(Guid? tenantId, DateTime from, DateTime to, BucketSize bucketSize, CancellationToken ct = default)
            => Fail<Task<DiagnosticsReport>>();
        public Task<DimensionReport> GetDimensionReportAsync(Guid? tenantId, DateTime from, DateTime to, DimensionGroup groupBy, CancellationToken ct = default)
            => Fail<Task<DimensionReport>>();
        public Task<ProviderDiagnosticsDeepReport> GetDeepReportAsync(Guid? tenantId, DateTime from, DateTime to, string? providerKey, CancellationToken ct = default)
            => Fail<Task<ProviderDiagnosticsDeepReport>>();
        public Task<BudgetStatus> GetBudgetAsync(Guid accountId, CancellationToken ct = default) => Fail<Task<BudgetStatus>>();
        public IReadOnlyList<ProviderDiagnostic> GetRecentEvents(Guid? tenantId, int limit = 50) => Fail<IReadOnlyList<ProviderDiagnostic>>();
    }

    private sealed class ThrowingDiagnosticsRepository : IDiagnosticsRepository
    {
        public bool Called { get; private set; }

        private T Fail<T>()
        {
            Called = true;
            throw new InvalidOperationException("repository must not be called after a fail-closed guard");
        }

        public Task<Guid> InsertAsync(ProviderDiagnostic diagnostic) => Fail<Task<Guid>>();
        public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(string? providerKey, DateTime? from, DateTime? to, int limit, int offset)
            => Fail<Task<(List<ProviderDiagnostic>, int)>>();
        public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(string? providerKey, DateTime? from, DateTime? to, int limit, int offset, Guid? tenantId, bool? success, string? model)
            => Fail<Task<(List<ProviderDiagnostic>, int)>>();
        public Task<decimal> GetCostSumAsync(Guid? tenantId, DateTime from, DateTime to) => Fail<Task<decimal>>();
        public Task<List<DiagnosticsBucketRow>> AggregateAsync(DateTime from, DateTime to, TimeSpan bucket, Guid? tenantId) => Fail<Task<List<DiagnosticsBucketRow>>>();
        public Task<List<DiagnosticsDetailRow>> FetchDetailAsync(DateTime from, DateTime to, Guid? tenantId, string? providerKey) => Fail<Task<List<DiagnosticsDetailRow>>>();
    }
}
