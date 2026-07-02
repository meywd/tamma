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
/// Story 36-2 — iterates active tenants from the CP directory and, per tenant,
/// runs the dimensional projection (<see cref="ComputeTenantDimensionalRollupActivity"/>),
/// then — when the target hour is the final hour of a UTC day — the lossless
/// daily compaction (<see cref="CompactDailyAnalyticsActivity"/>), then the
/// best-effort hourly retention purge (<see cref="PurgeStaleUsageAnalyticsActivity"/>).
///
/// <para>Mirrors <see cref="FanOutTenantRollupsActivity"/>: a single activity
/// looping the compute helpers serially (NOT an Elsa composite — that would
/// materialise one workflow instance per tenant and explode the journal on
/// large fleets). Serial keeps the per-tenant pool cache hot. Per-tenant
/// failures are caught, emitted as
/// <c>ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_FAILED</c>, counted, and the loop
/// continues (Story 28-10 AC5 tolerance).</para>
///
/// <para><b>Design note (single tenant iteration):</b> compaction and the
/// usage purge are per-tenant helpers driven inside this one fan-out rather
/// than as separate top-level workflow steps, so the workflow adds exactly one
/// new sequence step and each tenant's schema is opened once per pass. The
/// per-tenant compaction/purge are still best-effort — a compaction/purge
/// failure for a tenant does not fail that tenant's projection nor the
/// fan-out.</para>
///
/// <para><b>Projection-lag SLO (AC13):</b> after the loop the activity records
/// the wall-clock lag between the rolled-up <c>hour</c> and completion; when it
/// exceeds <see cref="SloLagBudgetSeconds"/> (default 2h) it emits
/// <c>ANALYTICS.ROLLUP.DIMENSIONAL_LAG</c> + updates the
/// <c>tamma.analytics.projection_lag_seconds</c> OTel gauge. A breach is a
/// WARN, never a failure.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Fan Out Tenant Dimensional Rollups",
    "Iterate active tenants and run the dimensional projection + daily compaction + usage purge inline.",
    Kind = ActivityKind.Task)]
public sealed class FanOutTenantDimensionalRollupsActivity : TammaAsyncActivity
{
    /// <summary>Default projection-lag SLO budget — 2 hours (Story 36-2 AC13).</summary>
    public const int DefaultSloLagBudgetSeconds = 2 * 60 * 60;

    [Input(Description = "UTC top-of-hour bucket this rollup targets.")]
    public Input<DateTime> Hour { get; set; } = default!;

    [Input(Description = "Reset each tenant's dimensional checkpoint (backfill).")]
    public Input<bool> ResetCheckpoint { get; set; } = new(false);

    [Input(Description = "Projection-lag SLO budget in seconds (default 2 hours).")]
    public Input<int> SloLagBudgetSeconds { get; set; } = new(DefaultSloLagBudgetSeconds);

    [Output(Description = "Number of tenants whose dimensional rollup succeeded.")]
    public Output<int> TenantsSuccess { get; set; } = default!;

    [Output(Description = "Number of tenants whose dimensional rollup threw.")]
    public Output<int> TenantsFailed { get; set; } = default!;

    public override string? EventType => "ANALYTICS.ROLLUP.DIMENSIONAL_FANOUT";

    /// <summary>
    /// Test seam — bypass the real per-tenant compute so the fan-out loop can
    /// be exercised without a real tenant DB.
    /// </summary>
    internal Func<Guid, DateTime, CancellationToken, Task>? ComputeOneOverride { get; set; }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var hour = AnalyticsRollupEvents.TruncateToHour(Hour.Get(context));
        var reset = ResetCheckpoint.Get(context);
        var budget = SloLagBudgetSeconds.Get(context);
        Logger ??= context.GetService<ILogger<FanOutTenantDimensionalRollupsActivity>>();

        var cpFactory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var tenantFactory = context.GetRequiredService<ITenantDbContextFactory>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        var pricing = context.GetService<IAnalyticsPricingConfig>() ?? new NullAnalyticsPricingConfig();
        var metrics = context.GetService<DimensionalProjectionMetrics>();

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
            "analytics.dimensional.fanout_starting hour={Hour} tenantCount={Count}",
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
                    await ProjectOneTenantAsync(
                        tenantFactory, publisher, pricing, tenantId, hour, reset, Logger,
                        context.CancellationToken).ConfigureAwait(false);
                }
                success++;
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Logger?.LogWarning(ex,
                    "analytics.dimensional.tenant_failed tenantId={TenantId} hour={Hour}",
                    tenantId, hour);

                try
                {
                    await publisher.AppendAndPublishAsync(
                        AnalyticsRollupEvents.BuildEvent(
                            AnalyticsRollupEvents.TenantDimensionalRollupFailed,
                            hour,
                            tenantId,
                            data: new Dictionary<string, object?>
                            {
                                ["errorType"] = ex.GetType().Name,
                                ["message"] = ex.Message,
                            }),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort emission — see FanOutTenantRollupsActivity.
                }
            }
        }

        TenantsSuccess.Set(context, success);
        TenantsFailed.Set(context, failed);

        Logger?.LogInformation(
            "analytics.dimensional.fanout_completed hour={Hour} success={Success} failed={Failed}",
            hour, success, failed);

        // ── Projection-lag SLO (AC13) ──
        var lagSeconds = (DateTime.UtcNow - hour).TotalSeconds;
        if (lagSeconds < 0) lagSeconds = 0;
        metrics?.RecordLag(lagSeconds);

        if (lagSeconds > budget)
        {
            Logger?.LogWarning(
                "analytics.dimensional.lag_over_slo hour={Hour} lagSeconds={Lag} budgetSeconds={Budget}",
                hour, lagSeconds, budget);
            try
            {
                await publisher.AppendAndPublishAsync(
                    AnalyticsRollupEvents.BuildEvent(
                        AnalyticsRollupEvents.DimensionalLag,
                        hour,
                        tenantId: null,
                        data: new Dictionary<string, object?>
                        {
                            ["lagSeconds"] = lagSeconds,
                            ["hour"] = hour.ToString("O"),
                            ["budgetSeconds"] = budget,
                        }),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // SLO signal is best-effort — never fail the pass on emission.
            }
        }
    }

    /// <summary>
    /// Per-tenant work: dimensional projection, then (at the last hour of a UTC
    /// day) the lossless daily compaction, then the best-effort hourly purge.
    /// Compaction/purge failures are best-effort and do not fail the tenant's
    /// projection. Exposed for the pure-DI / backfill path.
    /// </summary>
    public static async Task ProjectOneTenantAsync(
        ITenantDbContextFactory tenantFactory,
        IPlatformEventPublisher publisher,
        IAnalyticsPricingConfig pricing,
        Guid tenantId,
        DateTime hour,
        bool resetCheckpoint,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        await ComputeTenantDimensionalRollupActivity.ComputeAsync(
            tenantFactory, publisher, tenantId, hour, pricing, resetCheckpoint, logger,
            cancellationToken).ConfigureAwait(false);

        // Daily compaction runs when the final hour (23:00) of a UTC day has
        // been projected — that day is now complete.
        if (hour.Hour == 23)
        {
            try
            {
                await CompactDailyAnalyticsActivity.CompactAsync(
                    tenantFactory, publisher, tenantId,
                    CompactDailyAnalyticsActivity.TruncateToDay(hour), logger, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "analytics.compact.daily_failed tenantId={TenantId} day={Day}",
                    tenantId, CompactDailyAnalyticsActivity.TruncateToDay(hour));
            }
        }

        // Best-effort hourly retention purge (runs last for the tenant).
        try
        {
            await PurgeStaleUsageAnalyticsActivity.PurgeAsync(
                tenantFactory, publisher, tenantId, DateTime.UtcNow,
                PurgeStaleUsageAnalyticsActivity.DefaultRetentionMonths, logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "analytics.purge.usage_hourly_failed tenantId={TenantId}", tenantId);
        }
    }
}
