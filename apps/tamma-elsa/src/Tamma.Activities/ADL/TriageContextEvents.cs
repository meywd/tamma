namespace Tamma.Activities.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageContextGathering.md</c> §5 #4) — central
/// catalogue of the <c>TRIAGE.CONTEXT.*</c> DCB event types emitted by the
/// <c>triage-context-gathering</c> workflow. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors the
/// sibling <see cref="TriageEvents"/> catalogue used by the panel.
///
/// <para>The context-gathering stage runs one tool-enabled <c>context-scan</c>
/// (mediated through the central <c>llm-call</c> seam — no provider key ever enters
/// the engine) to gather triage-time context for a single untriaged item (code
/// usage of the affected package/module, dependency graph, CVE details, changelog
/// / migration guides). Each lifecycle transition is an auditable event so
/// time-travel debugging can reconstruct whether context was genuinely gathered,
/// degraded to an unstructured blob, or failed entirely — rather than a failed
/// scan being silently coalesced to <c>{}</c> and presented downstream as a false
/// success.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitTriageEventActivity"/>, <see cref="EmitPrEventActivity"/>
/// and <see cref="EmitMergeApprovalEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a
/// directly-injected <c>IEventRepository</c> would be inert and silently drop every
/// context event). The drain resolves the tenant from the workflow's
/// <c>TenantId</c> variable, so a SaaS caller's context events carry the tenant
/// tag.</para>
///
/// <list type="bullet">
///   <item><description><c>TRIAGE.CONTEXT.STARTED</c> — the stage began gathering
///     context for an item (tags carry repository + item number + item type).</description></item>
///   <item><description><c>TRIAGE.CONTEXT.COMPLETED</c> — the scan produced a usable
///     structured context bundle (success).</description></item>
///   <item><description><c>TRIAGE.CONTEXT.EMPTY</c> — the scan succeeded but yielded
///     no usable structured context (only free-form prose / an empty object). A
///     degraded — not failed — result; loud (warning-status) so the panel / PO can
///     down-weight it rather than reasoning over phantom context.</description></item>
///   <item><description><c>TRIAGE.CONTEXT.FAILED</c> — the mediated <c>llm-call</c>
///     reported failure (all providers failed) so no context was gathered. Loud
///     (error-status). The stage reports <c>contextStatus="failed"</c> so the
///     parent cycle routes to a non-applying terminal instead of running the panel
///     over empty context. This is the no-false-success / no-empty-fallback rule
///     made explicit: a failed scan is NEVER coalesced to <c>"{}"</c> reported as a
///     success.</description></item>
/// </list>
/// </summary>
public static class TriageContextEvents
{
    public const string Started = "TRIAGE.CONTEXT.STARTED";
    public const string Completed = "TRIAGE.CONTEXT.COMPLETED";
    public const string Empty = "TRIAGE.CONTEXT.EMPTY";
    public const string Failed = "TRIAGE.CONTEXT.FAILED";

    /// <summary>The <c>contextStatus</c> values the stage reports on its output so
    /// the parent cycle can branch on context health. <c>ok</c> / <c>empty</c> are
    /// usable (panel may still run); <c>failed</c> means no context was gathered and
    /// the cycle must NOT run the panel over phantom context.</summary>
    public const string StatusOk = "ok";
    public const string StatusEmpty = "empty";
    public const string StatusFailed = "failed";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (context events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="TriageEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Failure-status check: a failed scan is a loud (error-status) audit row, an
    /// empty (degraded) scan is a warning-status row, a completed scan is success.
    /// Mirrors <see cref="TriageEvents.StatusForEvent"/>'s intent of never recording
    /// a degraded/failed outcome as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        Failed => "error",
        Empty => "warning",
        _ => "success",
    };

    /// <summary>
    /// Map a terminal <c>contextStatus</c> string to the corresponding DCB event
    /// type. <c>failed</c> → FAILED, <c>empty</c> → EMPTY, anything else (ok) →
    /// COMPLETED. Keeps the success-path emit node and the status variable reading
    /// the same single source of truth.
    /// </summary>
    public static string EventTypeForStatus(string contextStatus) => contextStatus switch
    {
        StatusFailed => Failed,
        StatusEmpty => Empty,
        _ => Completed,
    };
}
