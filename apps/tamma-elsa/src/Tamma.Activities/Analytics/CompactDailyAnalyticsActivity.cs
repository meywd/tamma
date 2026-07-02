using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Core.Enums;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 36-2 — rolls a tenant's <c>analytics_usage_hourly</c> rows for a
/// completed UTC day up into <c>analytics_usage_daily</c> as a lossless
/// <c>GROUP BY date_trunc('day', Hour), &lt;all dims&gt;</c> with summed
/// measures, upserting on the daily <c>UX_analytics_usage_daily_dims</c>
/// business key.
///
/// <para>Lossless because Story 36-1 gave the hourly and daily entities an
/// identical dimension + measure contract. Idempotent (whole-day
/// read-then-upsert overwrite) so a re-run is a no-op on measures. Runs once
/// per day — the workflow only schedules it when the target hour is the first
/// hour of a new UTC day, compacting the day that just ended — so no second
/// scheduler is introduced.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Compact Daily Analytics",
    "Roll analytics_usage_hourly into analytics_usage_daily for a completed UTC day.",
    Kind = ActivityKind.Task)]
public sealed class CompactDailyAnalyticsActivity : TammaAsyncActivity
{
    [Input(Description = "Tenant id to compact.")]
    public Input<Guid> TenantId { get; set; } = default!;

    [Input(Description = "UTC midnight of the day to compact (the day that just ended).")]
    public Input<DateTime> Day { get; set; } = default!;

    public override string? EventType => "ANALYTICS.COMPACT";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var tenantId = TenantId.Get(context);
        var day = TruncateToDay(Day.Get(context));

        Logger ??= context.GetService<ILogger<CompactDailyAnalyticsActivity>>();

        var tenantFactory = context.GetRequiredService<ITenantDbContextFactory>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        await CompactAsync(tenantFactory, publisher, tenantId, day, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — reads the day's hourly rows, groups by the full
    /// dimension tuple, and upserts the summed measures into the daily table.
    /// </summary>
    public static async Task CompactAsync(
        ITenantDbContextFactory tenantFactory,
        IPlatformEventPublisher publisher,
        Guid tenantId,
        DateTime day,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantFactory);
        ArgumentNullException.ThrowIfNull(publisher);

        day = TruncateToDay(day);
        var dayEnd = day.AddDays(1);

        await using var tenantDb = await tenantFactory
            .CreateAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var hourly = await tenantDb.AnalyticsUsageHourly
            .AsNoTracking()
            .Where(r => r.Hour >= day && r.Hour < dayEnd)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Lossless GROUP BY the full dimension tuple (done in memory — a day is
        // ≤ 24 × tuple-count rows, trivially bounded).
        var grouped = hourly
            .GroupBy(r => (r.Provider, r.AgentId, r.WorkflowDefinitionId, r.RepoId, r.CostBasis))
            .ToList();

        var now = DateTime.UtcNow;
        long rowsWritten = 0;

        foreach (var g in grouped)
        {
            var (provider, agentId, workflowDefinitionId, repoId, costBasis) = g.Key;

            var existing = await tenantDb.AnalyticsUsageDaily
                .FirstOrDefaultAsync(
                    r => r.Day == day
                         && r.Provider == provider
                         && r.AgentId == agentId
                         && r.WorkflowDefinitionId == workflowDefinitionId
                         && r.RepoId == repoId
                         && r.CostBasis == costBasis,
                    cancellationToken)
                .ConfigureAwait(false);

            var target = existing ?? new AnalyticsUsageDaily
            {
                Id = Guid.NewGuid(),
                Day = day,
                Provider = provider,
                AgentId = agentId,
                WorkflowDefinitionId = workflowDefinitionId,
                RepoId = repoId,
                CostBasis = costBasis,
            };

            target.TokensIn = g.Sum(r => r.TokensIn);
            target.TokensOut = g.Sum(r => r.TokensOut);
            target.CostUsd = g.Sum(r => r.CostUsd);
            target.PlatformBilledUsd = g.Sum(r => r.PlatformBilledUsd);
            target.WorkflowsStarted = g.Sum(r => r.WorkflowsStarted);
            target.WorkflowsCompleted = g.Sum(r => r.WorkflowsCompleted);
            target.WorkflowsFailed = g.Sum(r => r.WorkflowsFailed);
            target.AgentDispatches = g.Sum(r => r.AgentDispatches);
            target.ComputedAt = now;

            if (existing is null) tenantDb.AnalyticsUsageDaily.Add(target);
            rowsWritten++;
        }

        await tenantDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.DailyCompacted,
                day,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["day"] = day.ToString("O"),
                    ["rowsWritten"] = rowsWritten,
                }),
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "analytics.compact.daily_completed tenantId={TenantId} day={Day} rowsWritten={Rows}",
            tenantId, day, rowsWritten);
    }

    /// <summary>Truncate to UTC midnight.</summary>
    public static DateTime TruncateToDay(DateTime instant)
    {
        var utc = instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
    }
}
