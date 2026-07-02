using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 36-2 — per-tenant retention sweeper for
/// <c>analytics_usage_hourly</c>. Mirrors
/// <see cref="PurgeStaleAnalyticsActivity"/> (which purges the CP
/// <c>platform_analytics_hourly</c>) but targets the tenant's own hourly fact
/// table via <see cref="ITenantDbContextFactory"/>.
///
/// <para>Deletes rows whose <c>Hour</c> bucket is older than the retention
/// window (13 months by default, Doc 04 §7) via <c>ExecuteDeleteAsync</c>.
/// Daily rows retain on the longer daily window and are NOT touched here.</para>
///
/// <para><b>Best-effort:</b> runs last in the sequence, after the fresh bucket
/// and the daily compaction are persisted. A purge failure is logged +
/// emitted as <c>ANALYTICS.PURGE.USAGE_HOURLY_FAILED</c> but never rethrown,
/// so a transient tenant-DB hiccup cannot fail a rollup that already wrote
/// useful rows.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Purge Stale Usage Analytics",
    "Delete analytics_usage_hourly rows older than the retention window (default 13 months).",
    Kind = ActivityKind.Task)]
public sealed class PurgeStaleUsageAnalyticsActivity : TammaAsyncActivity
{
    /// <summary>Doc 04 §7 — 13-month hourly analytics retention window.</summary>
    public const int DefaultRetentionMonths = 13;

    [Input(Description = "Tenant id whose hourly usage rows to purge.")]
    public Input<Guid> TenantId { get; set; } = default!;

    [Input(Description =
        "Retention window in months. Rows older than now minus this window are "
        + "deleted. Non-positive values fall back to the 13-month default.")]
    public Input<int> RetentionMonths { get; set; } = new(DefaultRetentionMonths);

    public override string? EventType => "ANALYTICS.PURGE.USAGE";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        Logger ??= context.GetService<ILogger<PurgeStaleUsageAnalyticsActivity>>();

        var tenantFactory = context.GetRequiredService<ITenantDbContextFactory>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        var tenantId = TenantId.Get(context);
        var months = RetentionMonths.Get(context);

        try
        {
            await PurgeAsync(
                tenantFactory, publisher, tenantId, DateTime.UtcNow, months, Logger,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Host shutdown / cancellation is not a purge failure — propagate.
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort — never fail the parent rollup.
            Logger?.LogWarning(ex,
                "analytics.purge.usage_hourly_failed tenantId={TenantId} retentionMonths={Months}",
                tenantId, months);
            try
            {
                await publisher.AppendAndPublishAsync(
                    AnalyticsRollupEvents.BuildEvent(
                        AnalyticsRollupEvents.UsageHourlyPurgeFailed,
                        AnalyticsRollupEvents.TruncateToHour(DateTime.UtcNow),
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
                // Emitting the failure event is itself best-effort.
            }
        }
    }

    /// <summary>
    /// Pure-DI entry point — deletes stale rows for one tenant and emits the
    /// terminal <c>ANALYTICS.PURGE.USAGE_HOURLY</c> event. Returns the number
    /// of rows deleted.
    /// </summary>
    public static async Task<int> PurgeAsync(
        ITenantDbContextFactory tenantFactory,
        IPlatformEventPublisher publisher,
        Guid tenantId,
        DateTime nowUtc,
        int retentionMonths,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantFactory);
        ArgumentNullException.ThrowIfNull(publisher);

        var cutoff = ComputeCutoff(nowUtc, retentionMonths);

        await using var tenantDb = await tenantFactory
            .CreateAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var deleted = await tenantDb.AnalyticsUsageHourly
            .Where(r => r.Hour < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.UsageHourlyPurged,
                AnalyticsRollupEvents.TruncateToHour(nowUtc),
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["cutoff"] = cutoff.ToString("O"),
                    ["rowsDeleted"] = deleted,
                }),
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "analytics.purge.usage_hourly_completed tenantId={TenantId} cutoff={Cutoff} rowsDeleted={Deleted}",
            tenantId, cutoff, deleted);

        return deleted;
    }

    /// <summary>
    /// Inclusive staleness boundary. Non-positive windows clamp to the
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
