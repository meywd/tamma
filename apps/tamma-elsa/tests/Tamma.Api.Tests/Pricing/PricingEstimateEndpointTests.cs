using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-5 (AC10, AC14) — <see cref="PricingEndpoints.GetEstimate"/> priced
/// under the caller's plan + <c>(tenant, provider)</c> mode. Direct handler calls
/// with a real engine (frozen <see cref="ProviderPricingService"/> cost basis) +
/// a real <see cref="MarginPolicyResolver"/> over an InMemory context seeded with
/// the global 1.3x policy; the tenant/plan/mode seams are fakes so the isolation
/// contract (each tenant's own mode) is exercised deterministically.
/// </summary>
[TestFixture]
public class PricingEstimateEndpointTests
{
    private static readonly DateTime Epoch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ControlPlaneDbContext SeededContext()
    {
        var db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MarginPolicies.Add(new MarginPolicy
        {
            Id = Guid.NewGuid(),
            Scope = "global",
            RefKey = null,
            MarkupMultiplier = 1.3m,
            EffectiveFrom = Epoch,
            Status = "active",
        });
        db.SaveChanges();
        return db;
    }

    private static Task<IResult> Invoke(
        ControlPlaneDbContext db,
        string? provider,
        string? model,
        int inputTokens,
        int outputTokens,
        Guid? tenantId,
        TammaMode mode,
        PricingMode pricingMode)
    {
        var engine = new UsagePricingEngine(new ProviderPricingService(), NullLogger<UsagePricingEngine>.Instance);
        var resolver = new MarginPolicyResolver(db, NullLogger<MarginPolicyResolver>.Instance);

        return PricingEndpoints.GetEstimate(
            provider, model, inputTokens, outputTokens,
            new FakeTenantContext { TenantId = tenantId },
            new FakeModeProvider(mode),
            new FakePlanCatalog(),
            new FakeModeResolver((_, _) => pricingMode),
            resolver,
            engine,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            default);
    }

    private static object Value(IResult result)
    {
        result.Should().BeAssignableTo<IValueHttpResult>();
        return ((IValueHttpResult)result).Value!;
    }

    private static decimal Dec(object value, string prop) =>
        (decimal)value.GetType().GetProperty(prop)!.GetValue(value)!;

    private static string Str(object value, string prop) =>
        (string)value.GetType().GetProperty(prop)!.GetValue(value)!;

    [Test]
    public async Task GetEstimate_PlatformProvidedTenant_ReturnsMarkedUpPrice()
    {
        await using var db = SeededContext();

        var result = await Invoke(
            db, "anthropic", "claude-sonnet-4-20250514", 1000, 500,
            Guid.NewGuid(), TammaMode.SaaS, PricingMode.PlatformProvided);

        var value = Value(result);
        var cost = Dec(value, "costBasisUsd");
        var sell = Dec(value, "sellPriceUsd");
        var margin = Dec(value, "marginUsd");

        // 1000*3/1e6 + 500*15/1e6 = 0.0105 cost basis; *1.3 = 0.01365 sell.
        cost.Should().Be(0.010500m);
        sell.Should().Be(0.013650m);
        margin.Should().Be(0.003150m);
        Str(value, "pricingMode").Should().Be("PlatformProvided");
    }

    [Test]
    public async Task GetEstimate_ByokTenant_ReturnsZeroTokenSellPrice()
    {
        await using var db = SeededContext();

        var result = await Invoke(
            db, "anthropic", "claude-sonnet-4-20250514", 1000, 500,
            Guid.NewGuid(), TammaMode.SaaS, PricingMode.Byok);

        var value = Value(result);
        Dec(value, "costBasisUsd").Should().Be(0.010500m); // still computed
        Dec(value, "sellPriceUsd").Should().Be(0m);
        Dec(value, "marginUsd").Should().Be(0m);
        Str(value, "pricingMode").Should().Be("Byok");
    }

    [Test]
    public async Task GetEstimate_UnknownModel_Returns400()
    {
        await using var db = SeededContext();

        var result = await Invoke(
            db, "anthropic", "totally-made-up-model", 1000, 500,
            Guid.NewGuid(), TammaMode.SaaS, PricingMode.PlatformProvided);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task GetEstimate_TenantIsolation_EachTenantUsesItsOwnMode()
    {
        await using var db = SeededContext();
        var tenantPlatform = Guid.NewGuid();
        var tenantByok = Guid.NewGuid();

        var engine = new UsagePricingEngine(new ProviderPricingService(), NullLogger<UsagePricingEngine>.Instance);
        var resolver = new MarginPolicyResolver(db, NullLogger<MarginPolicyResolver>.Instance);

        // A mode resolver that keys strictly off the tenant id — the only way the
        // handler can pick the right mode is by passing the caller's own tenant.
        var modeResolver = new FakeModeResolver((tid, _) =>
            tid == tenantByok ? PricingMode.Byok : PricingMode.PlatformProvided);

        async Task<object> EstimateFor(Guid tenant)
        {
            var res = await PricingEndpoints.GetEstimate(
                "anthropic", "claude-sonnet-4-20250514", 1000, 500,
                new FakeTenantContext { TenantId = tenant },
                new FakeModeProvider(TammaMode.SaaS),
                new FakePlanCatalog(), modeResolver, resolver, engine,
                TimeProvider.System, NullLoggerFactory.Instance, default);
            return Value(res);
        }

        var platformValue = await EstimateFor(tenantPlatform);
        var byokValue = await EstimateFor(tenantByok);

        Str(platformValue, "pricingMode").Should().Be("PlatformProvided");
        Dec(platformValue, "sellPriceUsd").Should().Be(0.013650m);

        Str(byokValue, "pricingMode").Should().Be("Byok");
        Dec(byokValue, "sellPriceUsd").Should().Be(0m);
    }

    [Test]
    public async Task GetEstimate_MissingProvider_Returns400()
    {
        await using var db = SeededContext();

        var result = await Invoke(
            db, null, "claude-sonnet-4-20250514", 1000, 500,
            Guid.NewGuid(), TammaMode.SaaS, PricingMode.PlatformProvided);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class FakeModeProvider : ITammaModeProvider
    {
        public FakeModeProvider(TammaMode mode) => Mode = mode;
        public TammaMode Mode { get; }
    }

    private sealed class FakeModeResolver : ITenantProviderPricingModeResolver
    {
        private readonly Func<Guid?, string, PricingMode> _resolve;
        public FakeModeResolver(Func<Guid?, string, PricingMode> resolve) => _resolve = resolve;
        public Task<PricingMode> ResolveModeAsync(Guid? tenantId, string provider, CancellationToken ct = default) =>
            Task.FromResult(_resolve(tenantId, provider));
    }

    private sealed class FakePlanCatalog : IPlanCatalogService
    {
        public Task<PlanSnapshot?> GetActiveBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<PlanSnapshot?>(null);
        public Task<PlanSnapshot?> GetByIdAsync(Guid planId, CancellationToken ct = default) =>
            Task.FromResult<PlanSnapshot?>(null);
        public Task<PlanSnapshot?> GetForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<PlanSnapshot?>(null);
        public Task<IReadOnlyList<PlanSnapshot>> ListActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlanSnapshot>>(Array.Empty<PlanSnapshot>());
        public Task<IReadOnlyList<PlanSnapshot>> GetVersionsBySlugAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlanSnapshot>>(Array.Empty<PlanSnapshot>());
    }
}
