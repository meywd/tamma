using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 36-2 — per-tenant <b>dimensional</b> rollup step. Projects one
/// tenant's hour of <c>domain_events</c> (<c>LLM.CALL.SUCCESS</c>,
/// <c>AGENT.DISPATCH.*</c>) + <see cref="ProviderDiagnostic"/> rows into
/// <c>analytics_usage_hourly</c> — one row per
/// <c>(Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis)</c> tuple.
///
/// <para>Mirrors <see cref="ComputeTenantRollupActivity"/> exactly (static
/// pure-DI <see cref="ComputeAsync"/>, shared JSON measure extraction,
/// read-then-upsert on the business key) but GROUPS BY the dimension tuple
/// instead of collapsing to one row, and writes to the tenant's own schema
/// via <see cref="ITenantDbContextFactory"/> instead of the control-plane
/// fact table.</para>
///
/// <para><b>Idempotency:</b> the activity recomputes the entire
/// <c>(tenant, hour)</c> bucket from source each pass and <i>overwrites</i>
/// the measures on the full dimension business key — so replay and backfill
/// never double-count. The Story 36-1 <c>UX_analytics_usage_hourly_dims</c>
/// NULLS-NOT-DISTINCT index is the concurrent-replay backstop.</para>
///
/// <para><b>Resumable HIGH-WATER checkpoint:</b> a per-tenant
/// <see cref="AnalyticsProjectionCheckpoint"/> (<c>Stream = "dimensional"</c>)
/// records the max <see cref="DomainEvent.SequenceNumber"/> folded. When a fact
/// row already exists for the bucket AND no domain event newer than the
/// checkpoint is present (<c>maxSeq &lt;= checkpoint</c>), the recompute is
/// SKIPPED — work happens only for events with <c>SequenceNumber &gt;
/// checkpoint</c>. Otherwise the whole bucket is recomputed and the checkpoint
/// advances to <c>maxSeq</c> in the same <c>SaveChanges</c>. The whole-bucket
/// overwrite (not a delta) is the idempotency mechanism, so a stale/reset
/// checkpoint can never corrupt totals; the checkpoint only skips redundant work.</para>
///
/// <para><b>Provider is NULLABLE</b> (Story 36-2): the projection stores both
/// provider-attributed usage (<c>LLM.CALL.SUCCESS</c>,
/// <see cref="ProviderDiagnostic"/> — always carry a provider) AND
/// non-provider-attributed counts. Agent-dispatch events
/// (<c>AGENT_DISPATCH.RUN_TRIGGERED.*</c> / legacy <c>AGENT.DISPATCH.*</c>) and
/// workflow-lifecycle counts carry no provider, so they bucket under the NULL
/// provider (as their own dimensional rows), NOT dropped. Absent nullable
/// dimensions (<c>agent_id</c>, <c>workflowDefinitionId</c>, <c>repoId</c>) also
/// bucket under <c>NULL</c>, so per-dimension breakdowns reconcile to the grand
/// total via the <c>UX_*_dims</c> NULLS-NOT-DISTINCT index.</para>
///
/// <para><b>Cost is authoritative from <see cref="ProviderDiagnostic"/>.</b>
/// No <c>LLM.CALL.SUCCESS</c> emitter exists in the engine today; diagnostics are
/// the sole cost/token source. To future-proof against double-counting when both
/// describe the same call, an <c>LLM.CALL.SUCCESS</c> event's cost is folded ONLY
/// when NO diagnostic shares its <c>correlationId</c>.</para>
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Compute Tenant Dimensional Rollup",
    "Project one tenant's domain_events + ProviderDiagnostic for a single hour into analytics_usage_hourly.",
    Kind = ActivityKind.Task)]
public sealed class ComputeTenantDimensionalRollupActivity : TammaAsyncActivity
{
    [Input(Description = "Tenant id to roll up.")]
    public Input<Guid> TenantId { get; set; } = default!;

    [Input(Description = "UTC top-of-hour bucket this rollup targets.")]
    public Input<DateTime> Hour { get; set; } = default!;

    [Input(Description = "Reset the dimensional checkpoint before re-projecting (backfill).")]
    public Input<bool> ResetCheckpoint { get; set; } = new(false);

    public override string? EventType => "ANALYTICS.ROLLUP.TENANT_DIMENSIONAL";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var tenantId = TenantId.Get(context);
        var hour = AnalyticsRollupEvents.TruncateToHour(Hour.Get(context));
        var reset = ResetCheckpoint.Get(context);

        Logger ??= context.GetService<ILogger<ComputeTenantDimensionalRollupActivity>>();

        var tenantFactory = context.GetRequiredService<ITenantDbContextFactory>();
        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        var pricing = context.GetService<IAnalyticsPricingConfig>() ?? new NullAnalyticsPricingConfig();

        await ComputeAsync(
            tenantFactory, publisher, tenantId, hour, pricing, reset, Logger, context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure-DI entry point — the fan-out, the backfill endpoint, and unit
    /// tests drive the same read-compute-upsert cycle without an
    /// <see cref="ActivityExecutionContext"/> (mirrors
    /// <see cref="ComputeTenantRollupActivity.ComputeAsync"/>).
    /// </summary>
    public static async Task ComputeAsync(
        ITenantDbContextFactory tenantFactory,
        IPlatformEventPublisher publisher,
        Guid tenantId,
        DateTime hour,
        IAnalyticsPricingConfig pricing,
        bool resetCheckpoint,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantFactory);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(pricing);

        hour = AnalyticsRollupEvents.TruncateToHour(hour);
        var hourEnd = hour.AddHours(1);

        await using var tenantDb = await tenantFactory
            .CreateAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        // ── Read the hour window from source (whole-bucket recompute) ──
        // Dispatch events come in TWO real families: the Story 38-2 mediation
        // events are underscore-prefixed (AGENT_DISPATCH.RUN_TRIGGERED.*) and the
        // legacy alert/analytics family is dotted (AGENT.DISPATCH.*). A dotted
        // LIKE pattern would NEVER match the underscore family (and worse, '_' is
        // a LIKE single-char wildcard), so we use StartsWith — EF escapes the '_'
        // and it translates on both Npgsql and the InMemory provider. Only the
        // RUN_TRIGGERED terminal (one per dispatch) counts; RUN_POLLED /
        // RESULTS_COLLECTED are follow-up ops, not dispatches, and are excluded.
        var events = await tenantDb.DomainEvents
            .AsNoTracking()
            .Where(e => e.CreatedAt >= hour && e.CreatedAt < hourEnd
                        && (e.Type == "LLM.CALL.SUCCESS"
                            || e.Type.StartsWith("AGENT.DISPATCH.")
                            || e.Type.StartsWith("AGENT_DISPATCH.RUN_TRIGGERED.")))
            .Select(e => new { e.Type, e.Tags, e.Data, e.SequenceNumber })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var diagnostics = await tenantDb.ProviderDiagnostics
            .AsNoTracking()
            .Where(d => d.CreatedAt >= hour && d.CreatedAt < hourEnd)
            .Select(d => new
            {
                d.ProviderKey, d.AgentType, d.ProjectId, d.CorrelationId,
                d.InputTokens, d.OutputTokens, d.TokensUsed, d.Cost,
                d.BillingMode,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // ── High-water skip (AC7): recompute only when a domain event newer than
        //    the checkpoint exists (or a reset is requested / no fact row yet). ──
        var maxSequence = events.Count > 0 ? events.Max(e => e.SequenceNumber) : 0L;

        var checkpoint = await tenantDb.AnalyticsProjectionCheckpoints
            .FirstOrDefaultAsync(c => c.Stream == AnalyticsProjectionCheckpoint.DimensionalStream,
                cancellationToken)
            .ConfigureAwait(false);

        var factRowExists = await tenantDb.AnalyticsUsageHourly
            .AsNoTracking()
            .AnyAsync(r => r.Hour == hour, cancellationToken)
            .ConfigureAwait(false);

        if (!resetCheckpoint && checkpoint is not null && factRowExists
            && maxSequence <= checkpoint.LastSequenceNumber)
        {
            // No event with SequenceNumber > checkpoint and the bucket is already
            // projected — nothing to recompute. The whole-bucket overwrite would
            // reproduce identical rows; skipping avoids the redundant write.
            logger?.LogInformation(
                "analytics.dimensional.skip_no_new_events tenantId={TenantId} hour={Hour} checkpoint={Checkpoint} maxSeq={MaxSeq}",
                tenantId, hour, checkpoint.LastSequenceNumber, maxSequence);
            return;
        }

        // The set of correlationIds already accounted for by an authoritative
        // ProviderDiagnostic — used to dedupe LLM.CALL.SUCCESS cost (see below).
        var diagnosticCorrelationIds = diagnostics
            .Where(d => d.CorrelationId is not null)
            .Select(d => d.CorrelationId!.Value)
            .ToHashSet();

        var measures = new Dictionary<DimensionKey, Measures>();

        // 1) DCB events — LLM usage + agent dispatches, keyed by tag tuple.
        //    Provider/agent tags differ by family: LLM uses provider/agent_id;
        //    the alert dispatch family uses agentHandle; the mediation dispatch
        //    family carries neither (→ NULL provider bucket). All read uniformly.
        foreach (var e in events)
        {
            var tags = ParseTags(e.Tags);
            var provider = FirstNonEmpty(Tag(tags, "provider"), Tag(tags, "agentProvider"));
            var agentId = FirstNonEmpty(Tag(tags, "agent_id"), Tag(tags, "agentHandle"));

            var key = new DimensionKey(
                provider,
                agentId,
                ParseGuid(FirstNonEmpty(Tag(tags, "workflowDefinitionId"), Tag(tags, "definitionId"))),
                Tag(tags, "repoId"),
                ResolveCostBasis(Tag(tags, "billing_mode"), diagnosticBillingMode: null));

            if (e.Type == "LLM.CALL.SUCCESS")
            {
                // Cost/token dedup: ProviderDiagnostic is authoritative. Fold an
                // event's cost ONLY when no diagnostic shares its correlationId,
                // so the two sources describing one call never double-count.
                var corr = ParseGuid(Tag(tags, "correlationId"));
                if (corr is not null && diagnosticCorrelationIds.Contains(corr.Value))
                    continue;

                var (cost, tokensIn, tokensOut) =
                    ComputeTenantRollupActivity.ExtractLlmUsage(e.Data);
                var m = GetOrAdd(measures, key);
                m.CostUsd += cost;
                m.TokensIn += tokensIn;
                m.TokensOut += tokensOut;
            }
            else
            {
                // AGENT_DISPATCH.RUN_TRIGGERED.* / AGENT.DISPATCH.* — one dispatch.
                var m = GetOrAdd(measures, key);
                m.AgentDispatches += 1;
            }
        }

        // 2) ProviderDiagnostic rows — provider (ProviderKey), agent (AgentType),
        //    repo (ProjectId). Diagnostics carry no workflow-definition; NULL.
        //    BillingMode (Story 34-3 column, populated by the 35-2 tagger on the
        //    LLM-call path) is now the real per-call posture — a byok diagnostic
        //    buckets under CostBasis.Byok, no longer the always-Platform stopgap.
        foreach (var d in diagnostics)
        {
            if (string.IsNullOrWhiteSpace(d.ProviderKey)) continue;

            var key = new DimensionKey(
                d.ProviderKey,
                NullIfEmpty(d.AgentType),
                WorkflowDefinitionId: null,
                NullIfEmpty(d.ProjectId),
                ResolveCostBasis(billingModeTag: null, diagnosticBillingMode: d.BillingMode));

            var m = GetOrAdd(measures, key);
            // The dominant writer (LlmProxyService.RecordDiagnosticAsync) sets only
            // the back-compat TokensUsed total, leaving InputTokens/OutputTokens 0.
            // When the split is unset, attribute the total to TokensIn so token
            // volume isn't under-counted; when the split IS populated, use it
            // verbatim — never both, so no double-count.
            if (d.InputTokens == 0 && d.OutputTokens == 0 && d.TokensUsed > 0)
            {
                m.TokensIn += d.TokensUsed;
            }
            else
            {
                m.TokensIn += d.InputTokens;
                m.TokensOut += d.OutputTokens;
            }
            m.CostUsd += d.Cost;
        }

        // 3) Workflow lifecycle counts, per WorkflowDefinitionId (mirrors the
        //    28-10 Status-based counts). Provider is nullable, so each definition
        //    gets its OWN dimensional row keyed (Provider=NULL, AgentId=NULL,
        //    WorkflowDefinitionId=<def>, RepoId=NULL, CostBasis=Platform default) —
        //    they are NOT folded onto a provider-attributed usage row (which need
        //    not exist). If an LLM event happened to produce that exact all-NULL
        //    key too, GetOrAdd merges onto it (counts added once, no double-count).
        var workflowCounts = await tenantDb.WorkflowInstances
            .AsNoTracking()
            .Where(w => w.CreatedAt >= hour && w.CreatedAt < hourEnd)
            .GroupBy(w => w.DefinitionId)
            .Select(g => new
            {
                DefinitionId = g.Key,
                Started = g.LongCount(),
                Completed = g.LongCount(w => w.Status == "completed"),
                Failed = g.LongCount(w => w.Status == "failed"),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var wc in workflowCounts)
        {
            var key = new DimensionKey(
                Provider: null,
                AgentId: null,
                WorkflowDefinitionId: wc.DefinitionId,
                RepoId: null,
                CostBasis: ResolveCostBasis(billingModeTag: null, diagnosticBillingMode: null));

            var m = GetOrAdd(measures, key);
            m.WorkflowsStarted += wc.Started;
            m.WorkflowsCompleted += wc.Completed;
            m.WorkflowsFailed += wc.Failed;
        }

        // ── Upsert each tuple on the full business key (whole-bucket overwrite) ──
        var now = DateTime.UtcNow;
        long rowsWritten = 0;
        long totalTokensIn = 0, totalTokensOut = 0;
        decimal totalCost = 0m, totalBilled = 0m;

        foreach (var (key, m) in measures)
        {
            // NULL-provider rows (workflow/dispatch counts) carry no cost, so the
            // margin is immaterial; MarginFor gets an empty key rather than null.
            var billed = key.CostBasis == CostBasis.Platform
                ? Math.Round(m.CostUsd * (1 + pricing.MarginFor(key.Provider ?? string.Empty)), 4, MidpointRounding.AwayFromZero)
                : 0m;
            var cost = Math.Round(m.CostUsd, 4, MidpointRounding.AwayFromZero);

            var existing = await tenantDb.AnalyticsUsageHourly
                .FirstOrDefaultAsync(
                    r => r.Hour == hour
                         && r.Provider == key.Provider
                         && r.AgentId == key.AgentId
                         && r.WorkflowDefinitionId == key.WorkflowDefinitionId
                         && r.RepoId == key.RepoId
                         && r.CostBasis == key.CostBasis,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                tenantDb.AnalyticsUsageHourly.Add(new AnalyticsUsageHourly
                {
                    Id = Guid.NewGuid(),
                    Hour = hour,
                    Provider = key.Provider,
                    AgentId = key.AgentId,
                    WorkflowDefinitionId = key.WorkflowDefinitionId,
                    RepoId = key.RepoId,
                    CostBasis = key.CostBasis,
                    TokensIn = m.TokensIn,
                    TokensOut = m.TokensOut,
                    CostUsd = cost,
                    PlatformBilledUsd = billed,
                    WorkflowsStarted = m.WorkflowsStarted,
                    WorkflowsCompleted = m.WorkflowsCompleted,
                    WorkflowsFailed = m.WorkflowsFailed,
                    AgentDispatches = m.AgentDispatches,
                    ComputedAt = now,
                });
            }
            else
            {
                existing.TokensIn = m.TokensIn;
                existing.TokensOut = m.TokensOut;
                existing.CostUsd = cost;
                existing.PlatformBilledUsd = billed;
                existing.WorkflowsStarted = m.WorkflowsStarted;
                existing.WorkflowsCompleted = m.WorkflowsCompleted;
                existing.WorkflowsFailed = m.WorkflowsFailed;
                existing.AgentDispatches = m.AgentDispatches;
                existing.ComputedAt = now;
            }

            rowsWritten++;
            totalTokensIn += m.TokensIn;
            totalTokensOut += m.TokensOut;
            totalCost += cost;
            totalBilled += billed;
        }

        // ── Advance the checkpoint atomically with the upsert (read above) ──
        if (checkpoint is null)
        {
            checkpoint = new AnalyticsProjectionCheckpoint
            {
                Id = Guid.NewGuid(),
                Stream = AnalyticsProjectionCheckpoint.DimensionalStream,
                LastSequenceNumber = 0,
            };
            tenantDb.AnalyticsProjectionCheckpoints.Add(checkpoint);
        }

        var previous = checkpoint.LastSequenceNumber;
        // Whole-bucket overwrite is the idempotency mechanism; the checkpoint
        // only ever advances forward on the happy path. A reset re-bases it to
        // this bucket's max so an operator can re-drive an old window.
        checkpoint.LastSequenceNumber = resetCheckpoint
            ? maxSequence
            : Math.Max(previous, maxSequence);
        checkpoint.UpdatedAt = now;

        await tenantDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.TenantDimensionalRollupCompleted,
                hour,
                tenantId,
                data: new Dictionary<string, object?>
                {
                    ["rowsWritten"] = rowsWritten,
                    ["tuples"] = measures.Count,
                    ["tokensIn"] = totalTokensIn,
                    ["tokensOut"] = totalTokensOut,
                    ["costUsd"] = totalCost,
                    ["platformBilledUsd"] = totalBilled,
                    ["checkpoint"] = checkpoint.LastSequenceNumber,
                }),
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "analytics.dimensional.tenant_completed tenantId={TenantId} hour={Hour} rowsWritten={Rows} tuples={Tuples} tokensIn={In} tokensOut={Out} costUsd={Cost} platformBilledUsd={Billed} checkpoint={Checkpoint}",
            tenantId, hour, rowsWritten, measures.Count, totalTokensIn, totalTokensOut, totalCost, totalBilled, checkpoint.LastSequenceNumber);
    }

    /// <summary>
    /// Pure cost-basis resolver (Story 35-2 <c>billing_mode</c> signal). The
    /// event tag wins; the <see cref="ProviderDiagnostic"/> billing-mode column
    /// backs it. Absent/anything-but-<c>byok</c> → <see cref="CostBasis.Platform"/>
    /// (single-user / legacy events predating 35-2 are billed as platform).
    /// </summary>
    internal static CostBasis ResolveCostBasis(string? billingModeTag, string? diagnosticBillingMode)
    {
        var mode = FirstNonEmpty(billingModeTag, diagnosticBillingMode);
        return string.Equals(mode, "byok", StringComparison.OrdinalIgnoreCase)
            ? CostBasis.Byok
            : CostBasis.Platform;
    }

    private static Measures GetOrAdd(Dictionary<DimensionKey, Measures> map, DimensionKey key)
    {
        if (!map.TryGetValue(key, out var m))
        {
            m = new Measures();
            map[key] = m;
        }
        return m;
    }

    private static Dictionary<string, string?> ParseTags(string? tagsJson)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tagsJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }
        }
        catch (JsonException)
        {
            // Tolerate malformed tag blobs — same posture as measure extraction.
        }
        return result;
    }

    private static string? Tag(Dictionary<string, string?> tags, string key) =>
        tags.TryGetValue(key, out var v) ? NullIfEmpty(v) : null;

    private static string? NullIfEmpty(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static Guid? ParseGuid(string? raw) =>
        Guid.TryParse(raw, out var g) ? g : null;

    /// <summary>Immutable dimension tuple — the full business key. Provider is
    /// nullable: dispatch/workflow counts bucket under the NULL provider.</summary>
    private readonly record struct DimensionKey(
        string? Provider,
        string? AgentId,
        Guid? WorkflowDefinitionId,
        string? RepoId,
        CostBasis CostBasis);

    /// <summary>Mutable per-tuple measure accumulator.</summary>
    private sealed class Measures
    {
        public long TokensIn;
        public long TokensOut;
        public decimal CostUsd;
        public long WorkflowsStarted;
        public long WorkflowsCompleted;
        public long WorkflowsFailed;
        public long AgentDispatches;
    }
}
