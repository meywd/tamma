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
/// Story 28-10 — <c>PURGE_ANALYTICS_HOURLY</c> retention sweeper. Deletes
/// <c>platform_analytics_hourly</c> rows whose <c>Hour</c> bucket is older
/// than the retention window (13 months by default, matching the Doc 04
/// §7 SOC-2 analytics-retention requirement) so the fact table cannot
/// grow without bound.
///
/// <para>Runs as the final step of
/// <see cref="Tamma.ElsaServer.Workflows.HourlyAnalyticsRollupWorkflow"/>
/// — riding the existing hourly schedule + advisory lock means there is
/// no second scheduler to operate, and the per-hour delete is cheap
/// (indexed on <c>Hour</c>; after the first sweep only the single bucket
/// that just crossed the boundary qualifies).</para>
///
/// <para><b>Best-effort:</b> retention is housekeeping, never the point of
/// the run. A purge failure is logged + emitted as
/// <c>ANALYTICS.PURGE.FAILED</c> but never rethrown, so a transient CP
/// hiccup cannot fail an hourly rollup that already wrote useful rows.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Purge Stale Analytics",
    "Delete platform_analytics_hourly rows older than the retention window (default 13 months).",
    Kind = ActivityKind.Task)]
public sealed class PurgeStaleAnalyticsActivity : TammaAsyncActivity
{
    /// <summary>Doc 04 §7 — 13-month analytics retention window.</summary>
    public const int DefaultRetentionMonths = 13;

    [Input(Description =
        "Retention window in months. Rows older than now minus this window "
        + "are deleted. Non-positive values fall back to the 13-month default.")]
    public Input<int> RetentionMonths { get; set; } = new(DefaultRetentionMonths);

    public override string? EventType => "ANALYTICS.PURGE";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        Logger ??= context.GetService<ILogger<PurgeStaleAnalyticsActivity>>();

        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        var months = RetentionMonths.Get(context);

        try
        {
            await PurgeAsync(
                factory, publisher, DateTime.UtcNow, months, Logger, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — never fail the parent rollup. Record the
            // failure for the ops dashboard and move on.
            Logger?.LogWarning(ex,
                "analytics.purge.failed retentionMonths={Months}", months);
            try
            {
                await publisher.AppendAndPublishAsync(
                    AnalyticsRollupEvents.BuildEvent(
                        AnalyticsRollupEvents.AnalyticsPurgeFailed,
                        AnalyticsRollupEvents.TruncateToHour(DateTime.UtcNow),
                        tenantId: null,
                        data: new Dictionary<string, object?>
                        {
                            ["errorType"] = ex.GetType().Name,
                            ["message"] = ex.Message,
                        }),
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Emitting the failure event is itself best-effort.
            }
        }
    }

    /// <summary>
    /// Pure-DI entry point — deletes stale rows and emits the terminal
    /// <c>ANALYTICS.PURGE.HOURLY</c> event. Callable from an admin
    /// "force-purge" endpoint without a live Elsa execution context.
    /// Returns the number of rows deleted.
    /// </summary>
    public static async Task<int> PurgeAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        IPlatformEventPublisher publisher,
        DateTime nowUtc,
        int retentionMonths,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cpFactory);
        ArgumentNullException.ThrowIfNull(publisher);

        var cutoff = ComputeCutoff(nowUtc, retentionMonths);

        await using var cp = await cpFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var deleted = await cp.PlatformAnalyticsHourly
            .Where(r => r.Hour < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.AnalyticsPurged,
                AnalyticsRollupEvents.TruncateToHour(nowUtc),
                tenantId: null,
                data: new Dictionary<string, object?>
                {
                    ["cutoff"] = cutoff.ToString("O"),
                    ["rowsDeleted"] = deleted,
                }),
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "analytics.purge.completed cutoff={Cutoff} rowsDeleted={Deleted}",
            cutoff, deleted);

        return deleted;
    }

    /// <summary>
    /// Pure retention-policy helper: the inclusive boundary below which a
    /// bucket is considered stale. Non-positive windows clamp to the
    /// 13-month default so a misconfiguration can never delete live data.
    /// Always returns a UTC instant.
    /// </summary>
    public static DateTime ComputeCutoff(DateTime nowUtc, int retentionMonths)
    {
        var utc = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : DateTime.SpecifyKind(nowUtc.ToUniversalTime(), DateTimeKind.Utc);
        var months = retentionMonths > 0 ? retentionMonths : DefaultRetentionMonths;
        return DateTime.SpecifyKind(utc.AddMonths(-months), DateTimeKind.Utc);
    }
}
