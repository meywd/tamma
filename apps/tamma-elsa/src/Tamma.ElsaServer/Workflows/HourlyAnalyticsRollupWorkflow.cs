using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.Analytics;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 28-10 — hourly global-Elsa workflow that rolls
/// <c>platform_events</c> plus per-tenant <c>domain_events</c> into
/// <c>platform_analytics_hourly</c>.
///
/// <para>Schedule: <c>0 5 * * * *</c> (every hour at minute 5, UTC). The
/// 5-minute offset absorbs late-arriving events from long-running
/// workflows completing just before the top of the hour. The target
/// bucket is always "the hour that just ended" — at 12:05 we roll up
/// 11:00–12:00.</para>
///
/// <para>Fan-out shape:</para>
/// <list type="number">
///   <item><description><c>InitBucket</c> — derive the target hour from
///     <see cref="DateTime.UtcNow"/> (or the optional <c>hour</c> input
///     for backfill) and truncate to the top-of-hour.</description></item>
///   <item><description><c>ComputePlatformRollupActivity</c> — writes the
///     platform-wide row (<c>TenantId = null</c>) from
///     <c>platform_events</c> + tenants directory.</description></item>
///   <item><description><c>FanOutTenantRollupsActivity</c> — list active
///     tenants from the CP directory and run
///     <c>ComputeTenantRollupActivity</c> per tenant. Per-tenant failures
///     do NOT fail the parent (Story 28-10 AC5 — 5% tolerance); they
///     emit <c>ANALYTICS.ROLLUP.TENANT_FAILED</c> and the run
///     continues.</description></item>
///   <item><description><c>EmitHourCompleted</c> — single terminal event
///     with the aggregate success / failure counts so the ops dashboard
///     can see "1200 tenants rolled up at hour X".</description></item>
/// </list>
///
/// <para>This is a code-first <see cref="WorkflowBase"/> registered via
/// <c>AddWorkflowsFrom</c> in the global-Elsa host (Program.cs). The cron
/// trigger itself is attached via an operator-configured scheduled
/// trigger — until the scheduler lands (or a runbook admin chooses to
/// fire manually via <c>POST /workflows/run/hourly-analytics-rollup</c>),
/// the workflow is inert — exactly what we want: shipping the wiring
/// without turning the cron on, matching the Epic 28 rollout plan.</para>
/// </summary>
public class HourlyAnalyticsRollupWorkflow : WorkflowBase
{
    /// <summary>Definition id. Stable — the cron scheduler references this.</summary>
    public const string DefinitionId = "hourly-analytics-rollup";

    /// <summary>
    /// Default cron expression — minute 5 of every hour, UTC. Six-field
    /// form matching the Elsa scheduling package's Quartz-style parser
    /// (<c>second minute hour day month weekday</c>): run at second 0,
    /// minute 5 of every hour.
    /// </summary>
    public const string CronExpression = "0 5 * * * *";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Hourly Analytics Rollup";
        builder.DefinitionId = DefinitionId;
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Rolls platform_events + per-tenant domain_events into platform_analytics_hourly.";

        // ── Variables ────────────────────────────────────────────────────
        var targetHour = builder.WithVariable<DateTime>("TargetHour", DateTime.MinValue).Persisted();
        var tenantsSuccess = builder.WithVariable<int>("TenantsSuccess", 0).Persisted();
        var tenantsFailed = builder.WithVariable<int>("TenantsFailed", 0).Persisted();
        // Story 36-2 — dimensional fan-out success/failure counts.
        var dimTenantsSuccess = builder.WithVariable<int>("DimTenantsSuccess", 0).Persisted();
        var dimTenantsFailed = builder.WithVariable<int>("DimTenantsFailed", 0).Persisted();

        // ── Step 1: resolve the target hour from input or now-1 ─────────
        var initBucket = new SetVariable
        {
            Id = "InitBucket",
            Name = "Init Target Hour",
            Variable = targetHour,
            Value = new Input<object?>(ctx =>
            {
                var raw = ctx.GetInput<object?>("hour");
                DateTime instant;
                switch (raw)
                {
                    case DateTime dt:
                        instant = dt;
                        break;
                    case DateTimeOffset dto:
                        instant = dto.UtcDateTime;
                        break;
                    case string s when DateTime.TryParse(
                        s,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed):
                        instant = parsed;
                        break;
                    default:
                        // Default: roll up the hour that just ended.
                        instant = DateTime.UtcNow.AddHours(-1);
                        break;
                }
                return AnalyticsRollupEvents.TruncateToHour(instant);
            }),
        };

        // ── Step 2: platform-wide rollup (always first — cheap and does
        //    not depend on any tenant DB being reachable) ───────────────
        var platformRollup = new ComputePlatformRollupActivity
        {
            Id = "ComputePlatformRollup",
            Name = "Compute Platform Rollup",
            Hour = new Input<DateTime>(ctx => targetHour.Get(ctx)),
        };

        // ── Step 3: iterate active tenants + fan out ────────────────────
        var fanOut = new FanOutTenantRollupsActivity
        {
            Id = "FanOutTenantRollups",
            Name = "Fan Out Tenant Rollups",
            Hour = new Input<DateTime>(ctx => targetHour.Get(ctx)),
            TenantsSuccess = new Output<int>(tenantsSuccess),
            TenantsFailed = new Output<int>(tenantsFailed),
        };

        // ── Step 3b: per-tenant DIMENSIONAL rollup (Story 36-2) ─────────
        //    Runs after the platform fact-table fan-out, sharing the same
        //    schedule / advisory lock / target hour. Projects each tenant's
        //    domain_events + ProviderDiagnostic into its own
        //    analytics_usage_hourly (one row per provider/agent/workflow/repo/
        //    cost-basis tuple), then — at the last hour of a UTC day — the
        //    lossless daily compaction, then the best-effort hourly retention
        //    purge. Per-tenant failures are tolerated (they do NOT abort the
        //    fan-out). The existing platform rollup + CP purge are untouched.
        var fanOutDimensional = new FanOutTenantDimensionalRollupsActivity
        {
            Id = "FanOutTenantDimensionalRollups",
            Name = "Fan Out Tenant Dimensional Rollups",
            Hour = new Input<DateTime>(ctx => targetHour.Get(ctx)),
            TenantsSuccess = new Output<int>(dimTenantsSuccess),
            TenantsFailed = new Output<int>(dimTenantsFailed),
        };

        // ── Step 4: emit the terminal HOUR_COMPLETED event ──────────────
        var emitCompleted = new EmitHourCompletedActivity
        {
            Id = "EmitHourCompleted",
            Name = "Emit Hour Completed",
            Hour = new Input<DateTime>(ctx => targetHour.Get(ctx)),
            TenantsSuccess = new Input<int>(ctx => tenantsSuccess.Get(ctx)),
            TenantsFailed = new Input<int>(ctx => tenantsFailed.Get(ctx)),
        };

        // ── Step 5: PURGE_ANALYTICS_HOURLY — drop rows past the 13-month
        //    retention window. Best-effort (never throws) so it cannot fail
        //    a rollup that already wrote useful rows. Runs last so the
        //    fresh bucket is safely persisted before any deletion. ───────
        var purgeStale = new PurgeStaleAnalyticsActivity
        {
            Id = "PurgeStaleAnalytics",
            Name = "Purge Stale Analytics",
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                initBucket,
                platformRollup,
                fanOut,
                fanOutDimensional,
                emitCompleted,
                purgeStale,
            },
        };
    }
}
