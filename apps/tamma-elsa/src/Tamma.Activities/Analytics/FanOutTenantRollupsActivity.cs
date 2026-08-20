using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 28-10 — iterates active tenants from the CP directory and calls
/// <see cref="ComputeTenantRollupActivity.ComputeAsync"/> inline for each.
/// Emits <c>ANALYTICS.ROLLUP.TENANT_FAILED</c> for any tenant that throws
/// and keeps going (Story 28-10 AC5 — 5% tolerance).
///
/// <para>This is deliberately NOT a composite Elsa workflow — it is a
/// single activity that invokes the compute-one-tenant helper in a loop.
/// Rationale: Elsa's <c>Parallel</c> + <c>ForEach</c> composites would
/// materialise one workflow instance per tenant, which is overkill for a
/// read-only aggregation step and explodes the journal on 10k-tenant
/// fleets. The loop runs on a single workflow instance.</para>
///
/// <para>Concurrency: serial by design. At a 10k-tenant fleet, each
/// tenant rollup averages &lt; 100ms, so the full loop completes in
/// ~15 minutes — well inside the hourly budget. Parallelism would thrash
/// the per-tenant pool cache (Story 28-4) which caches at most 256 live
/// pools; serial keeps the cache hot as we sweep through.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Fan Out Tenant Rollups",
    "Iterate active tenants and call ComputeTenantRollupActivity inline.",
    Kind = ActivityKind.Task)]
public sealed class FanOutTenantRollupsActivity : TammaAsyncActivity
{
    [Input(Description = "UTC top-of-hour bucket this rollup targets.")]
    public Input<DateTime> Hour { get; set; } = default!;

    [Output(Description = "Number of tenants that rolled up successfully.")]
    public Output<int> TenantsSuccess { get; set; } = default!;

    [Output(Description = "Number of tenants whose rollup threw.")]
    public Output<int> TenantsFailed { get; set; } = default!;

    public override string? EventType => "ANALYTICS.ROLLUP.FANOUT";

    /// <summary>
    /// Test seam — unit tests assign a delegate that bypasses the real
    /// <see cref="ComputeTenantRollupActivity.ComputeAsync"/> call so
    /// the fan-out loop can be exercised without a real tenant DB.
    /// </summary>
    internal Func<Guid, DateTime, CancellationToken, Task>? ComputeOneOverride { get; set; }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var hour = AnalyticsRollupEvents.TruncateToHour(Hour.Get(context));
        Logger ??= context.GetService<ILogger<FanOutTenantRollupsActivity>>();

        var cpFactory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        // 2026-08-18 — DEGRADE, do not fault. This was GetRequiredService, so on a host
        // that composes no tenant data plane (the engine: ITenantDbContextFactory ships
        // from AddTammaData, which only Tamma.Api calls) the activity threw every hour and
        // ContinueWithIncidentsStrategy buried it. Refusing to start the whole scheduler
        // was the first attempt at a fix, but that also killed ComputePlatformRollupActivity
        // — which runs FIRST, needs only the control-plane factory, and was succeeding every
        // hour. The tenant fan-out is the only part that needs this seam, so it is the only
        // part that skips.
        var tenantFactory = context.GetService<ITenantDbContextFactory>();
        if (tenantFactory is null)
        {
            // Count the tenants that SHOULD have been covered as FAILED, so the
            // degradation is visible in the durable stream (review finding,
            // 2026-08-19): with 0/0 the HOUR_COMPLETED event read as "no active
            // tenants exist" and the ops dashboard's coverage query silently got
            // the wrong answer — the exact log-line-only posture this fix chain
            // set out to eliminate. The active-tenant directory needs only the
            // CP factory, which IS present here.
            var uncovered = await CountActiveTenantsAsync(cpFactory, hour, context.CancellationToken)
                .ConfigureAwait(false);
            Logger?.LogWarning(
                "analytics.rollup.tenant_fanout_skipped hour={Hour} uncoveredTenants={Uncovered} "
                + "reason=no_tenant_data_plane — this host composes no ITenantDbContextFactory, so "
                + "per-tenant rollups cannot run. The platform-wide rollup is unaffected.",
                hour, uncovered);
            TenantsSuccess.Set(context, 0);
            TenantsFailed.Set(context, uncovered);
            return;
        }

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        // Target set: active tenants whose row existed at hour-end. The
        // Status shadow column tracks the Epic 28 lifecycle; legacy rows
        // created before the shadow column existed have Status=null but
        // are still considered active because the pre-Epic-28 schema had
        // no concept of provisioning. Soft-deleted tenants are skipped
        // (the CP context filter handles this).
        List<Guid> tenantIds;
        await using (var cp = await cpFactory
            .CreateDbContextAsync(context.CancellationToken)
            .ConfigureAwait(false))
        {
            tenantIds = await cp.Tenants
                .IgnoreQueryFilters()
                .Where(t => t.DeletedAt == null)
                .Where(t => t.CreatedAt < hour.AddHours(1))
                .Where(t => EF.Property<string?>(t, "Status") == null
                            || EF.Property<string?>(t, "Status") == "active")
                .Select(t => t.Id)
                .ToListAsync(context.CancellationToken)
                .ConfigureAwait(false);
        }

        Logger?.LogInformation(
            "analytics.rollup.fanout_starting hour={Hour} tenantCount={Count}",
            hour, tenantIds.Count);

        var success = 0;
        var failed = 0;

        foreach (var tenantId in tenantIds)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (ComputeOneOverride is not null)
                {
                    await ComputeOneOverride(tenantId, hour, context.CancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ComputeTenantRollupActivity.ComputeAsync(
                        cpFactory,
                        tenantFactory,
                        publisher,
                        tenantId,
                        hour,
                        Logger,
                        context.CancellationToken).ConfigureAwait(false);
                }
                success++;
            }
            catch (Exception ex)
            {
                failed++;
                Logger?.LogWarning(
                    ex,
                    "analytics.rollup.tenant_failed tenantId={TenantId} hour={Hour}",
                    tenantId, hour);

                // Best-effort emission — one bad tenant + one flaky
                // publisher must not cascade to the next iteration.
                try
                {
                    await publisher.AppendAndPublishAsync(
                        AnalyticsRollupEvents.BuildEvent(
                            AnalyticsRollupEvents.TenantRollupFailed,
                            hour,
                            tenantId,
                            data: new Dictionary<string, object?>
                            {
                                ["errorType"] = ex.GetType().Name,
                                // Caller-controlled message — must not
                                // include tenant credentials; the compute
                                // activity reads only tenant DB data and
                                // does NOT surface connection strings in
                                // exception messages.
                                ["message"] = ex.Message,
                            }),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Intentionally swallowed — see comment above.
                }
            }
        }

        TenantsSuccess.Set(context, success);
        TenantsFailed.Set(context, failed);

        Logger?.LogInformation(
            "analytics.rollup.fanout_completed hour={Hour} success={Success} failed={Failed}",
            hour, success, failed);
    }
    /// <summary>
    /// The same active-tenant predicate the fan-out targets, used by the degraded
    /// path to report how many tenants went UNCOVERED. Kept byte-identical to the
    /// listing below so "uncovered" and "targeted" can never disagree.
    /// </summary>
    internal static async Task<int> CountActiveTenantsAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory, DateTime hour, CancellationToken ct)
    {
        await using var cp = await cpFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await cp.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.DeletedAt == null)
            .Where(t => t.CreatedAt < hour.AddHours(1))
            .Where(t => EF.Property<string?>(t, "Status") == null
                        || EF.Property<string?>(t, "Status") == "active")
            .CountAsync(ct)
            .ConfigureAwait(false);
    }
}

