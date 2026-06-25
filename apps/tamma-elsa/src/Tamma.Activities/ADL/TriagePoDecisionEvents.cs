namespace Tamma.Activities.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriagePODecision.md</c> #3) / Story 26-1 AC
/// ("Events: <c>TRIAGE.*</c>") — central catalogue of the
/// <c>TRIAGE.PO_DECISION.*</c> DCB event types emitted by the
/// <c>triage-po-decision</c> workflow. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>).
///
/// <para>The PO decision is the final triage step: it dispatches <c>llm-call</c>
/// (role=<c>product_owner</c>, action=<c>triage-intake</c>) and turns the result
/// into the applied decision (<c>priority</c>/<c>type</c>/<c>complexity</c>/
/// <c>automation</c>/<c>labels</c>/<c>comment</c>). Each lifecycle transition is an
/// auditable event so the audit trail sees a <b>skipped</b> (empty input) or
/// <b>failed</b> (LLM call failed) PO step rather than a silent false success — the
/// no-false-success / no-empty-fallback rule made explicit (a failed LLM call is
/// NEVER laundered into a clean <c>needs-human</c>/<c>priority-normal</c> applied
/// decision).</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitTriageEventActivity"/> and
/// <see cref="EmitTriageContextEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a
/// directly-injected <c>IEventRepository</c> would be inert and silently drop every
/// event).</para>
///
/// <list type="bullet">
///   <item><description><c>TRIAGE.PO_DECISION.STARTED</c> — the PO step began
///     (tags carry repository + item number).</description></item>
///   <item><description><c>TRIAGE.PO_DECISION.COMPLETED</c> — the LLM call
///     succeeded and a decision was produced (data carries
///     priority/type/complexity/automation + provider/cost). May still be an
///     <c>unparsed</c> needs-human-review decision when the model returned prose —
///     a completed-but-degraded outcome (warning-status), never a clean false
///     classification.</description></item>
///   <item><description><c>TRIAGE.PO_DECISION.FAILED</c> — the <c>llm-call</c>
///     reported failure (all providers down / budget exhausted / allowlist
///     reject). Loud (error-status). NO applied decision is fabricated; the
///     emitted decision is an explicit <c>llm-failed</c> marker labelled
///     <c>triage-failed</c>/<c>needs-human</c>.</description></item>
///   <item><description><c>TRIAGE.PO_DECISION.SKIPPED</c> — empty input
///     (<c>itemJson</c> blank/<c>{}</c>); the LLM call is short-circuited.
///     Warning-status — no spend on garbage, no fabricated decision.</description></item>
/// </list>
/// </summary>
public static class TriagePoDecisionEvents
{
    public const string Started = "TRIAGE.PO_DECISION.STARTED";
    public const string Completed = "TRIAGE.PO_DECISION.COMPLETED";
    public const string Failed = "TRIAGE.PO_DECISION.FAILED";
    public const string Skipped = "TRIAGE.PO_DECISION.SKIPPED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (PO-decision events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="TriageEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed PO step is a loud (error-status) audit row; a
    /// skipped step is a warning-status row; a completed step is success.
    /// Mirrors <see cref="TriageEvents.StatusForEvent"/> — a degraded/failed
    /// outcome is never recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        Failed => "error",
        Skipped => "warning",
        _ => "success",
    };
}
