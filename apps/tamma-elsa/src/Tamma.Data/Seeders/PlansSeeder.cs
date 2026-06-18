using Microsoft.EntityFrameworkCore;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Idempotently seeds the <c>plans</c> price-book referenced by
/// <c>tenants.PlanId</c>: <c>free</c>, <c>team</c>, <c>enterprise</c>. Story
/// 28-1 shipped the bare plan rows; Story 34-1 extends the seed with the typed
/// <see cref="PlanFeature"/> / <see cref="PlanEntitlement"/> /
/// <see cref="PlanPrice"/> children and the versioning columns
/// (<c>Version = 1</c>, <c>Status = active</c>).
///
/// <para>Stable UUIDs — baked into the seed so FK targets stay deterministic
/// across environments and integration tests can assert against known IDs.</para>
///
/// <para><b>Insert-missing-only, per row</b> (mirrors
/// <see cref="AgentEntitySeeder"/> + the convention system-defaults ownership
/// rule): each plan/feature/entitlement/price is inserted only when absent.
/// A re-run is a no-op and NEVER reverts an admin-edited row — the old
/// whole-table <c>AnyAsync</c> short-circuit is gone so children backfill onto
/// a DB that already had the bare v1 plan rows. The seeder does NOT emit DCB
/// events — seeding system defaults is not a user-driven state transition
/// (consistent with the agent seeder); <c>PLAN.VERSION.CREATED</c> /
/// <c>PLAN.DEPRECATED</c> are reserved for real edits via
/// <c>PlanVersionEditor</c>.</para>
/// </summary>
public static class PlansSeeder
{
    /// <summary>Stable sentinel UUID for the free plan — never change.</summary>
    public static readonly Guid FreePlanId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>Stable sentinel UUID for the team plan — never change.</summary>
    public static readonly Guid TeamPlanId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    /// <summary>Stable sentinel UUID for the enterprise plan — never change.</summary>
    public static readonly Guid EnterprisePlanId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    /// <summary>
    /// Inserts any missing plan rows + their typed children. Safe to call on
    /// every startup — per-row existence checks make re-runs a no-op and never
    /// revert admin edits.
    /// </summary>
    public static async Task SeedAsync(
        ControlPlaneDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var spec in s_seed)
        {
            changed |= await SeedPlanAsync(context, spec, now, cancellationToken);
        }

        if (changed)
        {
            // The seeder is the trusted system-defaults populate path: it does
            // insert-missing-only backfill of children onto an already-active
            // v1 plan (Story 28-1 shipped the bare plan rows; 34-1 backfills
            // the typed children). The Story 34-1 immutability interceptor on
            // ControlPlaneDbContext would otherwise (correctly) reject adding a
            // child to an active plan, so suppress it for just this save.
            context.SuppressPlanImmutabilityGuard = true;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            finally
            {
                context.SuppressPlanImmutabilityGuard = false;
            }
        }
    }

    private static async Task<bool> SeedPlanAsync(
        ControlPlaneDbContext context,
        PlanSeed spec,
        DateTime now,
        CancellationToken ct)
    {
        var changed = false;

        var planExists = await context.Plans
            .AsNoTracking()
            .AnyAsync(p => p.Id == spec.Id, ct);

        if (!planExists)
        {
            context.Plans.Add(new Plan
            {
                Id = spec.Id,
                Slug = spec.Slug,
                DisplayName = spec.DisplayName,
                Version = 1,
                Status = "active",
                IsCustom = false,
                BillingInterval = "monthly",
                SupersedesPlanId = null,
                MonthlyPriceUsd = spec.MonthlyPriceUsd,
                Quotas = spec.QuotasJson,
                IsActive = true,
                PlacementPolicy = spec.PlacementPolicy,
                CreatedAt = now,
                UpdatedAt = now,
            });
            changed = true;
        }

        // Children backfill independently — they may be missing on a DB that
        // already had the bare v1 plan row from Story 28-1's seed.
        foreach (var f in spec.Features)
        {
            if (!await context.PlanFeatures.AsNoTracking().AnyAsync(x => x.Id == f.Id, ct))
            {
                context.PlanFeatures.Add(new PlanFeature
                {
                    Id = f.Id,
                    PlanId = spec.Id,
                    FeatureKey = f.FeatureKey,
                    BoolValue = f.BoolValue,
                    StringValue = f.StringValue,
                });
                changed = true;
            }
        }

        foreach (var e in spec.Entitlements)
        {
            if (!await context.PlanEntitlements.AsNoTracking().AnyAsync(x => x.Id == e.Id, ct))
            {
                context.PlanEntitlements.Add(new PlanEntitlement
                {
                    Id = e.Id,
                    PlanId = spec.Id,
                    MetricKey = e.MetricKey,
                    LimitValue = e.LimitValue,
                    Period = e.Period,
                    OverageMode = e.OverageMode,
                });
                changed = true;
            }
        }

        foreach (var p in spec.Prices)
        {
            if (!await context.PlanPrices.AsNoTracking().AnyAsync(x => x.Id == p.Id, ct))
            {
                context.PlanPrices.Add(new PlanPrice
                {
                    Id = p.Id,
                    PlanId = spec.Id,
                    PricingMode = p.PricingMode,
                    RecurringUsd = p.RecurringUsd,
                    SeatUsd = p.SeatUsd,
                    MeteredComponent = p.MeteredComponent,
                });
                changed = true;
            }
        }

        return changed;
    }

    // Deterministic child ids: aaaaaaaa-<plan>-<kind>-…  so test fixtures and
    // the insert-missing-only check can both key off a known UUID.
    private static Guid ChildId(int plan, int kind, int seq) =>
        Guid.Parse($"aaaaaaaa-0000-0000-{plan:D4}-{kind:D8}{seq:D4}");

    private static readonly IReadOnlyList<PlanSeed> s_seed = BuildSeed();

    private static IReadOnlyList<PlanSeed> BuildSeed() =>
    [
        new PlanSeed(
            FreePlanId, "free", "Free", 0m, "shared",
            "{\"llmTokensPerMonth\":100000,\"concurrentWorkflows\":1,\"seats\":1}",
            Features:
            [
                new FeatureSeed(ChildId(1, 1, 1), "byok_allowed", BoolValue: false, StringValue: null),
                new FeatureSeed(ChildId(1, 1, 2), "support_tier", BoolValue: null, StringValue: "community"),
            ],
            Entitlements:
            [
                new EntitlementSeed(ChildId(1, 2, 1), EntitlementMetricKey.Seats, 1, "total", "block"),
                new EntitlementSeed(ChildId(1, 2, 2), EntitlementMetricKey.Agents, 2, "total", "block"),
                new EntitlementSeed(ChildId(1, 2, 3), EntitlementMetricKey.Repos, 1, "total", "block"),
                new EntitlementSeed(ChildId(1, 2, 4), EntitlementMetricKey.WorkflowRuns, 50, "monthly", "block"),
                new EntitlementSeed(ChildId(1, 2, 5), EntitlementMetricKey.LlmTokens, 100_000, "monthly", "block"),
            ],
            Prices:
            [
                new PriceSeed(ChildId(1, 3, 1), "platform_provided", 0m, 0m, "{}"),
                new PriceSeed(ChildId(1, 3, 2), "byok", 0m, 0m, "{}"),
            ]),

        new PlanSeed(
            TeamPlanId, "team", "Team", 49m, "shared",
            "{\"llmTokensPerMonth\":2000000,\"concurrentWorkflows\":10,\"seats\":10}",
            Features:
            [
                new FeatureSeed(ChildId(2, 1, 1), "byok_allowed", BoolValue: true, StringValue: null),
                new FeatureSeed(ChildId(2, 1, 2), "support_tier", BoolValue: null, StringValue: "standard"),
            ],
            Entitlements:
            [
                new EntitlementSeed(ChildId(2, 2, 1), EntitlementMetricKey.Seats, 10, "total", "block"),
                new EntitlementSeed(ChildId(2, 2, 2), EntitlementMetricKey.Agents, 20, "total", "block"),
                new EntitlementSeed(ChildId(2, 2, 3), EntitlementMetricKey.Repos, 25, "total", "block"),
                new EntitlementSeed(ChildId(2, 2, 4), EntitlementMetricKey.WorkflowRuns, 2_000, "monthly", "meter"),
                new EntitlementSeed(ChildId(2, 2, 5), EntitlementMetricKey.LlmTokens, 2_000_000, "monthly", "meter"),
            ],
            Prices:
            [
                new PriceSeed(ChildId(2, 3, 1), "platform_provided", 49m, 15m, "{}"),
                new PriceSeed(ChildId(2, 3, 2), "byok", 29m, 10m, "{}"),
            ]),

        new PlanSeed(
            EnterprisePlanId, "enterprise", "Enterprise", 499m, "dedicated",
            "{\"llmTokensPerMonth\":50000000,\"concurrentWorkflows\":100,\"seats\":-1}",
            Features:
            [
                new FeatureSeed(ChildId(3, 1, 1), "byok_allowed", BoolValue: true, StringValue: null),
                new FeatureSeed(ChildId(3, 1, 2), "support_tier", BoolValue: null, StringValue: "priority"),
            ],
            Entitlements:
            [
                // NULL limit = unlimited.
                new EntitlementSeed(ChildId(3, 2, 1), EntitlementMetricKey.Seats, null, "total", "allow"),
                new EntitlementSeed(ChildId(3, 2, 2), EntitlementMetricKey.Agents, null, "total", "allow"),
                new EntitlementSeed(ChildId(3, 2, 3), EntitlementMetricKey.Repos, null, "total", "allow"),
                new EntitlementSeed(ChildId(3, 2, 4), EntitlementMetricKey.WorkflowRuns, null, "monthly", "meter"),
                new EntitlementSeed(ChildId(3, 2, 5), EntitlementMetricKey.LlmTokens, 50_000_000, "monthly", "meter"),
            ],
            Prices:
            [
                new PriceSeed(ChildId(3, 3, 1), "platform_provided", 499m, 25m, "{}"),
                new PriceSeed(ChildId(3, 3, 2), "byok", 299m, 20m, "{}"),
            ]),
    ];

    private sealed record PlanSeed(
        Guid Id,
        string Slug,
        string DisplayName,
        decimal MonthlyPriceUsd,
        string PlacementPolicy,
        string QuotasJson,
        IReadOnlyList<FeatureSeed> Features,
        IReadOnlyList<EntitlementSeed> Entitlements,
        IReadOnlyList<PriceSeed> Prices);

    private sealed record FeatureSeed(
        Guid Id, string FeatureKey, bool? BoolValue, string? StringValue);

    private sealed record EntitlementSeed(
        Guid Id, EntitlementMetricKey MetricKey, long? LimitValue, string Period, string OverageMode);

    private sealed record PriceSeed(
        Guid Id, string PricingMode, decimal RecurringUsd, decimal SeatUsd, string MeteredComponent);
}
