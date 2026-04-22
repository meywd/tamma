using System.Globalization;
using System.Text.Json;
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
/// Story 28-10 — per-tenant hourly rollup step. Reads one tenant's
/// <c>domain_events</c> (via <see cref="ITenantDbContextFactory"/>) for
/// the target hour, aggregates into a
/// <see cref="PlatformAnalyticsHourly"/> row on the control-plane DB, and
/// upserts on the <c>(Hour, TenantId)</c> unique index so a replay is a
/// no-op.
///
/// <para>Per-tenant failures are caught at the workflow layer (the parent
/// fan-out tolerates 5% per-bucket loss — matches Story 28-10 AC5) so
/// this activity always throws on error; it never swallows. Callers wrap
/// the invocation in a <c>try/catch</c> and emit
/// <c>ANALYTICS.ROLLUP.TENANT_FAILED</c> with the tenant id.</para>
///
/// <para>Idempotency strategy: the activity reads the existing row (if
/// any) for <c>(Hour, TenantId)</c> and updates it in place, otherwise
/// inserts. The unique partial index
/// <c>UX_platform_analytics_hourly_Hour_TenantId</c> is the backstop —
/// concurrent replays of the same bucket on the same tenant collapse to
/// one row.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Compute Tenant Rollup",
    "Aggregate one tenant's domain_events for a single hour into platform_analytics_hourly.",
    Kind = ActivityKind.Task)]
public sealed class ComputeTenantRollupActivity : TammaAsyncActivity
{
    [Input(Description = "Tenant id to roll up.")]
    public Input<Guid> TenantId { get; set; } = default!;

    [Input(Description = "UTC top-of-hour bucket this rollup targets.")]
    public Input<DateTime> Hour { get; set; } = default!;

    public override string? EventType => "ANALYTICS.ROLLUP.TENANT";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var tenantId = TenantId.Get(context);
        var hour = AnalyticsRollupEvents.TruncateToHour(Hour.Get(context));

        Logger ??= context.GetService<ILogger<ComputeTenantRollupActivity>>();

        var cpFactory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        var tenantFactory = context.GetRequiredService<ITenantDbContextFactory>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();

        await ComputeAsync(
            cpFactory,
            tenantFactory,
            publisher,
            tenantId,
            hour,
            Logger,
            context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — does the same read-compute-upsert cycle as
    /// <see cref="RunAsync"/> but takes its dependencies explicitly so
    /// callers outside the Elsa activity graph (the fan-out activity,
    /// unit tests, the admin "rerun one tenant" endpoint) can drive it
    /// without a fake <see cref="ActivityExecutionContext"/>.
    /// </summary>
    public static async Task ComputeAsync(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        ITenantDbContextFactory tenantFactory,
        IPlatformEventPublisher publisher,
        Guid tenantId,
        DateTime hour,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cpFactory);
        ArgumentNullException.ThrowIfNull(tenantFactory);
        ArgumentNullException.ThrowIfNull(publisher);

        hour = AnalyticsRollupEvents.TruncateToHour(hour);
        var hourEnd = hour.AddHours(1);

        // ── Aggregate from the tenant's domain_events ──
        await using var tenantDb = await tenantFactory
            .CreateAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var workflowsStarted = await tenantDb.WorkflowInstances
            .AsNoTracking()
            .CountAsync(w => w.CreatedAt >= hour && w.CreatedAt < hourEnd, cancellationToken)
            .ConfigureAwait(false);

        var workflowsCompleted = await tenantDb.WorkflowInstances
            .AsNoTracking()
            .CountAsync(
                w => w.CreatedAt >= hour && w.CreatedAt < hourEnd && w.Status == "completed",
                cancellationToken)
            .ConfigureAwait(false);

        var workflowsFailed = await tenantDb.WorkflowInstances
            .AsNoTracking()
            .CountAsync(
                w => w.CreatedAt >= hour && w.CreatedAt < hourEnd && w.Status == "failed",
                cancellationToken)
            .ConfigureAwait(false);

        // AGENT.DISPATCH.* counted via a STARTS-WITH on the Type column.
        // EF.Functions.Like works against Postgres AND the InMemory
        // provider (which translates to a plain string comparison), so
        // unit tests and production share the same predicate.
        var agentDispatches = await tenantDb.DomainEvents
            .AsNoTracking()
            .CountAsync(
                e => e.CreatedAt >= hour && e.CreatedAt < hourEnd
                     && EF.Functions.Like(e.Type, "AGENT.DISPATCH.%"),
                cancellationToken)
            .ConfigureAwait(false);

        // LLM.CALL.SUCCESS data column carries { costUsd, inputTokens,
        // outputTokens } per Story 9-2. Pull rows (bounded by the hour
        // window — typically ≤ tens of thousands per tenant per hour)
        // and aggregate in memory so we share the same JSON parsing
        // logic as PlatformAnalyticsService.TryExtractCostUsd.
        var llmRows = await tenantDb.DomainEvents
            .AsNoTracking()
            .Where(e => e.CreatedAt >= hour && e.CreatedAt < hourEnd
                        && e.Type == "LLM.CALL.SUCCESS")
            .Select(e => e.Data)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var (costUsd, tokensIn, tokensOut) = AggregateLlmUsage(llmRows);

        // ── Upsert into the CP fact table ──
        await using var cp = await cpFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await cp.PlatformAnalyticsHourly
            .FirstOrDefaultAsync(
                r => r.Hour == hour && r.TenantId == tenantId,
                cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            cp.PlatformAnalyticsHourly.Add(new PlatformAnalyticsHourly
            {
                Id = Guid.NewGuid(),
                Hour = hour,
                TenantId = tenantId,
                WorkflowsStarted = workflowsStarted,
                WorkflowsCompleted = workflowsCompleted,
                WorkflowsFailed = workflowsFailed,
                AgentDispatches = agentDispatches,
                TokensIn = tokensIn,
                TokensOut = tokensOut,
                CostUsd = costUsd,
                ActiveTenantsAtHourEnd = 0,
                ComputedAt = now,
            });
        }
        else
        {
            existing.WorkflowsStarted = workflowsStarted;
            existing.WorkflowsCompleted = workflowsCompleted;
            existing.WorkflowsFailed = workflowsFailed;
            existing.AgentDispatches = agentDispatches;
            existing.TokensIn = tokensIn;
            existing.TokensOut = tokensOut;
            existing.CostUsd = costUsd;
            existing.ComputedAt = now;
        }

        await cp.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Emit a durable completion event so the runbook can see exactly
        // which tenant×hour tuples have been rolled up.
        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.TenantRollupCompleted,
                hour,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["workflowsStarted"] = workflowsStarted,
                    ["workflowsCompleted"] = workflowsCompleted,
                    ["workflowsFailed"] = workflowsFailed,
                    ["agentDispatches"] = agentDispatches,
                    ["tokensIn"] = tokensIn,
                    ["tokensOut"] = tokensOut,
                    ["costUsd"] = costUsd,
                }),
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "analytics.rollup.tenant_completed tenantId={TenantId} hour={Hour} workflowsStarted={Started} workflowsCompleted={Completed} workflowsFailed={Failed} tokensIn={In} tokensOut={Out} costUsd={Cost}",
            tenantId, hour, workflowsStarted, workflowsCompleted, workflowsFailed, tokensIn, tokensOut, costUsd);
    }

    /// <summary>
    /// Sums <c>costUsd</c>, <c>inputTokens</c>, <c>outputTokens</c> from
    /// a batch of <c>LLM.CALL.SUCCESS</c> data-column JSON blobs.
    /// Malformed rows are skipped (same tolerance as the live
    /// <c>PlatformAnalyticsService</c> so historical events predating
    /// Story 9-2 don't break the rollup).
    /// </summary>
    internal static (decimal CostUsd, long TokensIn, long TokensOut) AggregateLlmUsage(
        IEnumerable<string?> dataBlobs)
    {
        var cost = 0m;
        var tokensIn = 0L;
        var tokensOut = 0L;

        foreach (var blob in dataBlobs)
        {
            if (string.IsNullOrWhiteSpace(blob)) continue;
            try
            {
                using var doc = JsonDocument.Parse(blob);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                if (root.TryGetProperty("costUsd", out var costEl))
                    cost += ReadDecimal(costEl);

                if (root.TryGetProperty("inputTokens", out var inEl))
                    tokensIn += ReadLong(inEl);

                if (root.TryGetProperty("outputTokens", out var outEl))
                    tokensOut += ReadLong(outEl);
            }
            catch (JsonException)
            {
                // Skip malformed rows — see method doc-comment.
            }
        }

        return (Math.Round(cost, 4, MidpointRounding.AwayFromZero), tokensIn, tokensOut);
    }

    private static decimal ReadDecimal(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : 0m,
        JsonValueKind.String => decimal.TryParse(
            el.GetString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d) ? d : 0m,
        _ => 0m,
    };

    private static long ReadLong(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt64(out var n) ? n : 0L,
        JsonValueKind.String => long.TryParse(
            el.GetString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var n) ? n : 0L,
        _ => 0L,
    };
}
