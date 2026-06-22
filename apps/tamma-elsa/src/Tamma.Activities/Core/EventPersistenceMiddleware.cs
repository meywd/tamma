using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Pipelines.ActivityExecution;
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
    /// Flush the events that have not yet been persisted (those at index
    /// &gt;= the stored cursor). On a successful flush the cursor advances
    /// past them; on failure it stays put so the same events retry next call.
    /// Never throws — a failing/throwing <paramref name="flush"/> is treated
    /// as a non-persisted batch (cursor unchanged) and surfaced via
    /// <paramref name="onError"/>.
    /// </summary>
    /// <param name="props">The workflow transient-property bag.</param>
    /// <param name="flush">Persists the new events; returns <c>true</c> only
    /// on a fully-successful append. Throwing is treated as <c>false</c>.</param>
    /// <param name="onError">Invoked with the unflushed count when the flush
    /// did not fully succeed (for loud ERROR logging by the caller).</param>
    /// <returns>The number of events persisted by this call (0 on failure /
    /// nothing-to-do).</returns>
    public static async Task<int> FlushAsync(
        IDictionary<object, object> props,
        Func<IReadOnlyList<TammaEvent>, Task<bool>> flush,
        Action<int, Exception?>? onError = null)
    {
        if (props is null) return 0;
        if (!props.TryGetValue(EventsKey, out var raw) || raw is not List<TammaEvent> all || all.Count == 0)
            return 0;

        var cursor = props.TryGetValue(CursorKey, out var c) && c is int ci ? ci : 0;
        if (cursor < 0) cursor = 0;
        if (cursor >= all.Count) return 0; // nothing new since last flush

        // Snapshot the pending slice. Copy so a concurrent emit during the
        // await can't shift indices under us — the cursor advances by the
        // snapshot length, never past events we didn't send.
        var pending = all.GetRange(cursor, all.Count - cursor);

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
/// <c>ConfigureDefaultActivityExecutionPipeline(p =&gt; p.Use(...))</c>.
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
    /// </summary>
    public static Func<ActivityMiddlewareDelegate, ActivityMiddlewareDelegate> Create() =>
        next => async context =>
        {
            // Run the activity (and the rest of the inner pipeline) first so
            // its emitted events are in the list before we drain.
            await next(context);
            await DrainAsync(context);
        };

    /// <summary>
    /// Resolve the API client + tenant from the activity execution scope and
    /// flush the new tamma:events. Public for the round-trip middleware test.
    /// </summary>
    public static async Task DrainAsync(ActivityExecutionContext context)
    {
        var apiClient = context.GetService<TammaApiClient>();
        var logger = context.GetService<ILogger<TammaApiClient>>();

        if (apiClient is null)
        {
            // No API client wired (e.g. a pure-local CLI run with no API
            // plane). Leave the events + cursor untouched so a later, wired
            // flush still drains them. Not an error condition.
            return;
        }

        var tenantId = ResolveTenantId(context);
        var props = context.WorkflowExecutionContext.TransientProperties;

        await EventDrain.FlushAsync(
            props,
            flush: pending => apiClient.AppendEventsAsync(
                pending.Select(ToWireRecord).ToList(),
                tenantId,
                context.CancellationToken),
            onError: (count, ex) =>
            {
                // LOUD — this is the audit trail. Cursor stays put; retried
                // on the next activity's flush. Never rethrown.
                if (ex is not null)
                    logger?.LogError(ex,
                        "tamma.events.flush_failed count={Count} workflowInstanceId={WorkflowInstanceId} — events retained for retry",
                        count, context.WorkflowExecutionContext.Id);
                else
                    logger?.LogError(
                        "tamma.events.flush_failed count={Count} workflowInstanceId={WorkflowInstanceId} — append not fully persisted, events retained for retry",
                        count, context.WorkflowExecutionContext.Id);
            }).ConfigureAwait(false);
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
        return raw switch
        {
            Guid g when g != Guid.Empty => g,
            string s when Guid.TryParse(s, out var p) && p != Guid.Empty => p,
            _ => null,
        };
    }

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
