using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Seeders;

/// <summary>
/// Idempotently seeds the <c>plans</c> table with the three default plans
/// referenced by <c>tenants.PlanId</c>: <c>plan_free</c>, <c>plan_team</c>,
/// <c>plan_enterprise</c>. Story 28-1 ships the seed; later stories may
/// extend with additional plans via admin UI (Story 28-11).
///
/// <para>Stable UUIDs — baked into the seed so FK targets stay
/// deterministic across environments and integration tests can assert
/// against known IDs.</para>
///
/// <para>Run pattern: <see cref="SeedAsync"/> is invoked once at API
/// startup (or by Story 28-2's hosted service) after migrations apply.
/// The <c>EXISTS</c> short-circuit makes re-runs a no-op.</para>
/// </summary>
public static class PlansSeeder
{
    /// <summary>Stable UUIDv4 for the free plan — never change.</summary>
    public static readonly Guid FreePlanId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>Stable UUIDv4 for the team plan — never change.</summary>
    public static readonly Guid TeamPlanId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    /// <summary>Stable UUIDv4 for the enterprise plan — never change.</summary>
    public static readonly Guid EnterprisePlanId =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    /// <summary>
    /// Inserts the three default plan rows if absent. Safe to call on every
    /// startup — the row count check makes re-runs a no-op.
    /// </summary>
    public static async Task SeedAsync(
        ControlPlaneDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var anyExisting = await context.Plans.AnyAsync(cancellationToken);
        if (anyExisting)
        {
            return;
        }

        var now = DateTime.UtcNow;

        var seed = new[]
        {
            new Plan
            {
                Id = FreePlanId,
                Slug = "free",
                DisplayName = "Free",
                MonthlyPriceUsd = 0m,
                Quotas = "{\"llmTokensPerMonth\":100000,\"concurrentWorkflows\":1,\"seats\":1}",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Plan
            {
                Id = TeamPlanId,
                Slug = "team",
                DisplayName = "Team",
                MonthlyPriceUsd = 49m,
                Quotas = "{\"llmTokensPerMonth\":2000000,\"concurrentWorkflows\":10,\"seats\":10}",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Plan
            {
                Id = EnterprisePlanId,
                Slug = "enterprise",
                DisplayName = "Enterprise",
                MonthlyPriceUsd = 499m,
                Quotas = "{\"llmTokensPerMonth\":50000000,\"concurrentWorkflows\":100,\"seats\":-1}",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
        };

        await context.Plans.AddRangeAsync(seed, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
