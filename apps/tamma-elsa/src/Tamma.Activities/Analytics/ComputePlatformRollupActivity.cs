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
using Tamma.Data.Entities;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 28-10 — platform-wide rollup step. Aggregates the
/// cross-tenant signal from <c>platform_events</c> (agent dispatches,
/// tenant lifecycle, etc.) for the target hour into a single
/// <see cref="PlatformAnalyticsHourly"/> row with <c>TenantId = null</c>,
/// plus stamps the fleet size counter
/// (<see cref="PlatformAnalyticsHourly.ActiveTenantsAtHourEnd"/>) from
/// the tenants directory.
///
/// <para>Contrast with <see cref="ComputeTenantRollupActivity"/>: this
/// one touches ONLY the control plane (reads <c>platform_events</c>,
/// <c>tenants</c>; writes <c>platform_analytics_hourly</c>). It is
/// independent of any per-tenant connection, so it can run at the start
/// of the workflow even when some tenant DBs are unreachable.</para>
///
/// <para>Idempotency: upserts on the
/// <c>UX_platform_analytics_hourly_Hour_PlatformWide</c> partial unique
/// index (one row per hour when <c>TenantId IS NULL</c>). Replays
/// overwrite.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Compute Platform Rollup",
    "Aggregate platform_events + tenants directory for a single hour into the platform-wide row.",
    Kind = ActivityKind.Task)]
public sealed class ComputePlatformRollupActivity : TammaAsyncActivity
{
    [Input(Description = "UTC top-of-hour bucket this rollup targets.")]
    public Input<DateTime> Hour { get; set; } = default!;

    public override string? EventType => "ANALYTICS.ROLLUP.PLATFORM";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var hour = AnalyticsRollupEvents.TruncateToHour(Hour.Get(context));

        Logger ??= context.GetService<ILogger<ComputePlatformRollupActivity>>();

        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        await ComputeAsync(factory, publisher, hour, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — same aggregation pipeline as
    /// <see cref="RunAsync"/> but callable from unit tests or an
    /// admin "rerun platform row" endpoint without a live Elsa
    /// execution context.
    /// </summary>
    public static async Task ComputeAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        IPlatformEventPublisher publisher,
        DateTime hour,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cpFactory);
        ArgumentNullException.ThrowIfNull(publisher);

        hour = AnalyticsRollupEvents.TruncateToHour(hour);
        var hourEnd = hour.AddHours(1);

        await using var cp = await cpFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Platform-wide AGENT.DISPATCH.* events in the bucket. Matches
        // the pattern PlatformAnalyticsService uses for the live
        // aggregation so the fact table and the fallback path report
        // the same counter.
        var agentDispatches = await cp.PlatformEvents
            .AsNoTracking()
            .CountAsync(
                e => e.CreatedAt >= hour && e.CreatedAt < hourEnd
                     && EF.Functions.Like(e.Type, "AGENT.DISPATCH.%"),
                cancellationToken)
            .ConfigureAwait(false);

        // ActiveTenantsAtHourEnd — a gauge snapshot. The query filter
        // on the CP context already excludes soft-deleted tenants; we
        // additionally require the tenant row to have existed before
        // hour-end so tenants created after the bucket don't show up
        // in a historical rollup.
        var activeTenants = await cp.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.DeletedAt == null)
            .Where(t => t.CreatedAt < hourEnd)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await cp.PlatformAnalyticsHourly
            .FirstOrDefaultAsync(
                r => r.Hour == hour && r.TenantId == null,
                cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            cp.PlatformAnalyticsHourly.Add(new PlatformAnalyticsHourly
            {
                Id = Guid.NewGuid(),
                Hour = hour,
                TenantId = null,
                // Workflow counters are zero on the platform-wide row —
                // the per-tenant activity owns those. ActiveTenantsAtHourEnd
                // is the only gauge meaningful at the platform level
                // right now; AgentDispatches carries the cross-tenant
                // signal (e.g. SaaS shared dispatch queue).
                WorkflowsStarted = 0,
                WorkflowsCompleted = 0,
                WorkflowsFailed = 0,
                AgentDispatches = agentDispatches,
                TokensIn = 0,
                TokensOut = 0,
                CostUsd = 0m,
                ActiveTenantsAtHourEnd = activeTenants,
                ComputedAt = now,
            });
        }
        else
        {
            existing.AgentDispatches = agentDispatches;
            existing.ActiveTenantsAtHourEnd = activeTenants;
            existing.ComputedAt = now;
        }

        await cp.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.PlatformRollupCompleted,
                hour,
                tenantId: null,
                data: new Dictionary<string, object?>
                {
                    ["agentDispatches"] = agentDispatches,
                    ["activeTenantsAtHourEnd"] = activeTenants,
                }),
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "analytics.rollup.platform_completed hour={Hour} agentDispatches={Dispatches} activeTenants={Active}",
            hour, agentDispatches, activeTenants);
    }
}
