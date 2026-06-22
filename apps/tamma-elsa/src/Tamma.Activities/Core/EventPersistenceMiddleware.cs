using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Features;
using Elsa.Workflows.Pipelines.ActivityExecution;
using Elsa.Workflows.Pipelines.WorkflowExecution;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Core;

// ════════════════════════════════════════════════════════════════════════
// Durable DCB-event persistence drain
//
// TammaEventEmitter appends every emitted TammaEvent into the workflow's
// `tamma:events` transient list (see TammaActivity.cs). Nothing previously
// drained that list, and the event repositories aren't registered inside the
// Elsa engine (Tamma.ElsaServer can't reference Tamma.Api). So the audit
// trail was emitted but never persisted.
//
// This middleware runs the inner activity, then flushes the NEW tamma:events
// entries (since the last flush) to the API's POST /api/engine/events, which
// writes them into the tenant domain_events store.
//
// Robustness — this is the audit trail, so we do NOT lose events:
//   • Incremental per-activity flush (a long-running / looping / crashing
//     workflow still persists progress; not only at workflow completion).
//   • A flushed-count cursor in the transient props prevents re-sending
//     events that already persisted (dedup).
//   • On flush failure: log ERROR, DO NOT advance the cursor, DO NOT clear
//     the events — the next activity's flush retries them.
//   • The middleware NEVER throws out of the flush path: a persistence
//     hiccup must not break the workflow run.
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pure, Elsa-free drain logic over the workflow's transient-property bag.
/// Factored out so the cursor + retry + dedup semantics are unit-testable
/// without standing up an Elsa runtime (mirrors the <c>ICleanupStateStore</c>
/// testing seam).
/// </summary>
public static class EventDrain
{
    /// <summary>Transient-property key holding the <c>List&lt;TammaEvent&gt;</c>.</summary>
    public const string EventsKey = "tamma:events";

    /// <summary>Transient-property key holding the flushed-count cursor (int).</summary>
    public const string CursorKey = "tamma:events:flushedCount";

    /// <summary>
    /// Default cap on the number of pending events re-sent per flush. While
    /// the API is down the pending backlog grows by every activity; without a
    /// cap each subsequent flush re-POSTs the entire (growing) backlog —
    /// O(N²) payload over an outage. Capping the slice keeps each POST bounded
    /// and, with idempotent append, still drains the backlog over successive
    /// flushes once the API recovers (I3).
    /// </summary>
    public const int DefaultMaxBatch = 256;

    /// <summary>
    /// Flush the events that have not yet been persisted (those at index
    /// &gt;= the stored cursor), at most <paramref name="maxBatch"/> at a
    /// time. On a successful flush the cursor advances past the sent slice; on
    /// failure it stays put so the same events retry next call. Never throws —
    /// a failing/throwing <paramref name="flush"/> is treated as a
    /// non-persisted batch (cursor unchanged) and surfaced via
    /// <paramref name="onError"/>.
    /// </summary>
    /// <param name="props">The workflow transient-property bag.</param>
    /// <param name="flush">Persists the new events; returns <c>true</c> only
    /// on a fully-successful append. Throwing is treated as <c>false</c>.</param>
    /// <param name="onError">Invoked with the unflushed count when the flush
    /// did not fully succeed (for loud ERROR logging by the caller).</param>
    /// <param name="maxBatch">Maximum events sent in one flush. The slice is
    /// bounded so an API outage can't grow the per-POST payload unbounded
    /// (I3). Defaults to <see cref="DefaultMaxBatch"/>.</param>
    /// <returns>The number of events persisted by this call (0 on failure /
    /// nothing-to-do).</returns>
    public static async Task<int> FlushAsync(
        IDictionary<object, object> props,
        Func<IReadOnlyList<TammaEvent>, Task<bool>> flush,
        Action<int, Exception?>? onError = null,
        int maxBatch = DefaultMaxBatch)
    {
        if (props is null) return 0;
        if (!props.TryGetValue(EventsKey, out var raw) || raw is not List<TammaEvent> all || all.Count == 0)
            return 0;

        var cursor = props.TryGetValue(CursorKey, out var c) && c is int ci ? ci : 0;
        if (cursor < 0) cursor = 0;
        if (cursor >= all.Count) return 0; // nothing new since last flush

        // Snapshot the pending slice, capped at maxBatch (I3 — bound the
        // payload during an outage). Copy so a concurrent emit during the
        // await can't shift indices under us — the cursor advances by the
        // snapshot length, never past events we didn't send.
        if (maxBatch < 1) maxBatch = 1;
        var take = Math.Min(maxBatch, all.Count - cursor);
        var pending = all.GetRange(cursor, take);

        bool ok;
        try
        {
            ok = await flush(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError?.Invoke(pending.Count, ex);
            return 0; // cursor unchanged → retried next flush
        }

        if (!ok)
        {
            onError?.Invoke(pending.Count, null);
            return 0; // cursor unchanged → retried next flush
        }

        props[CursorKey] = cursor + pending.Count;
        return pending.Count;
    }
}

/// <summary>
/// Elsa activity-execution middleware that drains the <c>tamma:events</c>
/// transient list to the durable store after each activity runs. Registered
/// in <c>Tamma.ElsaServer/Program.cs</c> via
/// <see cref="EventPersistencePipelineExtensions.UseTammaEventPersistence"/>
/// — which appends this component to the FULL Elsa runtime default
/// activity-execution pipeline (so the activity invoker still runs and
/// activities actually execute), not a fresh pipeline that drops the
/// invoker.
///
/// <para>Installed via the <c>Use(Func&lt;...&gt;)</c> pipeline overload (not
/// the constructor-convention <c>UseMiddleware&lt;T&gt;</c>) so it resolves
/// <see cref="TammaApiClient"/> + the tenant per-call from the activity
/// execution scope — there is no captive dependency and the flush is fully
/// best-effort.</para>
/// </summary>
public static class EventPersistenceMiddleware
{
    /// <summary>
    /// Build the middleware component. Runs the inner activity first, then
    /// flushes any new tamma:events. The flush failure is logged loudly and
    /// swallowed — it must never break the workflow run.
    ///
    /// <para>The drain runs in a <c>finally</c> so a FAULTING activity (which
    /// emits a <c>*.FAILED</c> event then rethrows — the most audit-relevant
    /// event) still gets its events flushed before the exception propagates.
    /// The original activity exception is never suppressed.</para>
    /// </summary>
    public static Func<ActivityMiddlewareDelegate, ActivityMiddlewareDelegate> Create() =>
        next => async context =>
        {
            // Run the activity (and the rest of the inner pipeline) first so
            // its emitted events are in the list before we drain. try/finally
            // so a throwing activity's FAILED event still flushes (I1).
            try
            {
                await next(context);
            }
            finally
            {
                await DrainAsync(context);
            }
        };

    /// <summary>Transient-property key holding the consecutive-flush-failure
    /// count (int). Used to throttle the ERROR log during an API outage (I3):
    /// the first failure logs ERROR, subsequent consecutive failures log WARN
    /// so an outage doesn't emit one ERROR per activity.</summary>
    public const string FailureStreakKey = "tamma:events:failureStreak";

    /// <summary>
    /// Resolve the API client + tenant from the activity execution scope and
    /// flush the new tamma:events. Public for the round-trip middleware test.
    /// </summary>
    public static Task DrainAsync(ActivityExecutionContext context)
    {
        var apiClient = context.GetService<TammaApiClient>();
        var logger = context.GetService<ILogger<TammaApiClient>>();
        var tenantId = ResolveTenantId(context);
        var props = context.WorkflowExecutionContext.TransientProperties;
        var wfId = context.WorkflowExecutionContext.Id;
        return DrainCoreAsync(props, apiClient, logger, tenantId, wfId, context.CancellationToken);
    }

    /// <summary>
    /// Workflow-completion / suspension backstop (I2): resolve the API client
    /// + tenant from the WORKFLOW execution scope and flush any remaining
    /// tamma:events beyond the cursor. Guarantees the final activity's events
    /// (and any left after a failed mid-run flush) get a flush attempt even if
    /// no further activity runs. Public for the backstop test.
    /// </summary>
    public static Task DrainAsync(WorkflowExecutionContext context)
    {
        var apiClient = context.GetService<TammaApiClient>();
        var logger = context.GetService<ILogger<TammaApiClient>>();
        var tenantId = ResolveTenantId(context);
        return DrainCoreAsync(
            context.TransientProperties, apiClient, logger, tenantId, context.Id, context.CancellationToken);
    }

    /// <summary>
    /// Shared drain: flush the new tamma:events via the API client. Never
    /// throws. On failure the cursor stays put (retry next flush / at the
    /// workflow backstop) and the ERROR is throttled — first failure ERROR,
    /// subsequent consecutive failures WARN — so an outage doesn't emit one
    /// ERROR per activity (I3). A success resets the failure streak.
    /// </summary>
    private static async Task DrainCoreAsync(
        IDictionary<object, object> props,
        TammaApiClient? apiClient,
        ILogger? logger,
        Guid? tenantId,
        string workflowInstanceId,
        CancellationToken ct)
    {
        if (apiClient is null || props is null)
        {
            // No API client wired (e.g. a pure-local CLI run with no API
            // plane). Leave the events + cursor untouched so a later, wired
            // flush still drains them. Not an error condition.
            return;
        }

        var persisted = await EventDrain.FlushAsync(
            props,
            flush: pending => apiClient.AppendEventsAsync(
                pending.Select(ToWireRecord).ToList(),
                tenantId,
                ct),
            onError: (count, ex) =>
            {
                // Throttle: ERROR on the first failure of a streak, WARN on
                // subsequent consecutive failures (I3 — avoid N error logs
                // during an outage). Cursor stays put; retried. Never rethrown.
                var streak = props.TryGetValue(FailureStreakKey, out var s) && s is int si ? si : 0;
                props[FailureStreakKey] = streak + 1;

                const string template =
                    "tamma.events.flush_failed count={Count} streak={Streak} workflowInstanceId={WorkflowInstanceId} — events retained for retry";
                if (streak == 0)
                {
                    if (ex is not null) logger?.LogError(ex, template, count, streak + 1, workflowInstanceId);
                    else logger?.LogError(template, count, streak + 1, workflowInstanceId);
                }
                else
                {
                    if (ex is not null) logger?.LogWarning(ex, template, count, streak + 1, workflowInstanceId);
                    else logger?.LogWarning(template, count, streak + 1, workflowInstanceId);
                }
            }).ConfigureAwait(false);

        // A successful flush clears the failure streak so the next outage
        // starts loud again.
        if (persisted > 0 && props.TryGetValue(FailureStreakKey, out var st) && st is int streakNow && streakNow > 0)
            props[FailureStreakKey] = 0;
    }

    /// <summary>
    /// Project an engine <see cref="TammaEvent"/> to the wire record the API
    /// persists. Public so the round-trip test can assert the projection.
    /// </summary>
    public static EngineEventRecord ToWireRecord(TammaEvent e)
    {
        JsonElement? data = null;
        if (e.Data is { Count: > 0 })
        {
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(e.Data));
                data = doc.RootElement.Clone();
            }
            catch
            {
                data = null;
            }
        }

        Dictionary<string, string?>? tags = null;
        if (e.Tags is { Count: > 0 })
        {
            tags = new Dictionary<string, string?>();
            foreach (var kv in e.Tags)
                tags[kv.Key] = kv.Value?.ToString();
        }

        return new EngineEventRecord(
            Id: e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
            EventType: e.EventType,
            Status: e.Status,
            Error: e.Error,
            Timestamp: e.Timestamp,
            DurationMs: e.Duration?.TotalMilliseconds,
            ActivityId: e.ActivityId,
            ActivityName: e.ActivityName,
            WorkflowInstanceId: e.WorkflowInstanceId,
            IssueNumber: ExtractIssueNumber(e),
            Data: data,
            Tags: tags);
    }

    /// <summary>
    /// Tenant resolution from the workflow scope. Workflows stamp the active
    /// tenant in a <c>TenantId</c> (or legacy <c>AccountId</c>) workflow
    /// variable — the same key RecordDiagnosticsActivity reads. Returns
    /// <c>null</c> for single-process / local runs with no tenant set (the
    /// API then resolves the tenant from its own ambient context / header).
    /// </summary>
    private static Guid? ResolveTenantId(ActivityExecutionContext context)
    {
        var raw = context.GetVariable<object?>("TenantId")
                  ?? context.GetVariable<object?>("AccountId");
        return CoerceTenantId(raw);
    }

    /// <summary>
    /// Tenant resolution from the WORKFLOW scope (I2 backstop). Reads the same
    /// <c>TenantId</c> / <c>AccountId</c> variable via the workflow's
    /// expression scope.
    /// </summary>
    private static Guid? ResolveTenantId(WorkflowExecutionContext context)
    {
        var raw = context.ExpressionExecutionContext.GetVariableInScope("TenantId")
                  ?? context.ExpressionExecutionContext.GetVariableInScope("AccountId");
        return CoerceTenantId(raw);
    }

    private static Guid? CoerceTenantId(object? raw) => raw switch
    {
        Guid g when g != Guid.Empty => g,
        string s when Guid.TryParse(s, out var p) && p != Guid.Empty => p,
        _ => null,
    };

    private static int? ExtractIssueNumber(TammaEvent e)
    {
        if (e.Tags is not null && e.Tags.TryGetValue("issueNumber", out var t))
        {
            if (t is int i) return i;
            if (t is long l) return (int)l;
            if (t is string s && int.TryParse(s, out var p)) return p;
        }
        if (e.Data.TryGetValue("issueNumber", out var d))
        {
            if (d is int i) return i;
            if (d is long l) return (int)l;
            if (d is string s && int.TryParse(s, out var p)) return p;
            if (d is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji)) return ji;
        }
        return null;
    }
}

/// <summary>
/// Workflow-execution-pipeline middleware backstop (I2). Runs the workflow,
/// then flushes any tamma:events left beyond the cursor on completion or
/// suspension — so the final activity's events (or events left after a failed
/// mid-run activity flush) are guaranteed at least one flush attempt even when
/// no further activity runs. Idempotent append (stable per-event id) makes a
/// double-flush with the per-activity drain harmless.
/// </summary>
public class EventPersistenceWorkflowMiddleware(WorkflowMiddlewareDelegate next)
    : WorkflowExecutionMiddleware(next)
{
    public override async ValueTask InvokeAsync(WorkflowExecutionContext context)
    {
        try
        {
            await Next(context);
        }
        finally
        {
            await EventPersistenceMiddleware.DrainAsync(context).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Registration helpers that wire the DCB-event drain into the Elsa engine
/// WITHOUT replacing the framework default activity/workflow pipelines.
///
/// <para><b>Why not <c>ConfigureDefaultActivityExecutionPipeline(p =&gt;
/// p.Use(...))</c>?</b> In Elsa 3.5.3 that calls
/// <c>IActivityExecutionPipeline.Setup</c>, which builds a FRESH pipeline from
/// only the supplied components and discards the framework defaults — including
/// the activity invoker (<c>UseBackgroundActivityInvoker</c> /
/// <c>UseDefaultActivityInvoker</c>) that actually calls
/// <c>activity.ExecuteAsync</c>. With only the drain middleware installed, its
/// <c>await next(context)</c> hits the empty terminal delegate, NO activity
/// runs, nothing is emitted, and every workflow is a silent no-op. (It is also
/// resolved off the root scope, while the real pipeline is scoped and rebuilt
/// per run — so it never even took effect.)</para>
///
/// <para><see cref="UseTammaEventPersistence"/> instead re-installs the full
/// runtime default pipelines via <c>WithDefaultActivityExecutionPipeline</c> /
/// <c>WithDefaultWorkflowExecutionPipeline</c> and APPENDS the drain after the
/// invoker — so activities still execute, then the drain flushes their emitted
/// events. Must be called from inside the <c>AddElsa(elsa =&gt; ...)</c> lambda
/// (the <c>AppFeature</c> configurator runs last, after <c>ElsaFeature</c>'s
/// own default-pipeline wiring, so this is the authoritative final delegate).
/// </para>
/// </summary>
public static class EventPersistencePipelineExtensions
{
    /// <summary>
    /// Install the full Elsa default activity- and workflow-execution
    /// pipelines and append the DCB-event drain so the audit trail persists
    /// after every activity (and on workflow completion/suspension) while the
    /// activity invoker still runs.
    /// </summary>
    public static WorkflowsFeature UseTammaEventPersistence(this WorkflowsFeature workflows)
    {
        workflows.WithDefaultActivityExecutionPipeline(pipeline =>
            pipeline.Use(EventPersistenceMiddleware.Create()));
        workflows.WithDefaultWorkflowExecutionPipeline(pipeline =>
            pipeline.UseMiddleware<EventPersistenceWorkflowMiddleware>());
        return workflows;
    }
}
