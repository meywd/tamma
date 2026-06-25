namespace Tamma.Activities.Blocker;

/// <summary>
/// Completeness audit 2026-06-22 (<c>BlockerDiagnosis.md</c> §Missing #3, 7-1G AC9) —
/// central catalogue of the <c>BLOCKER.*</c> DCB event types emitted by the
/// <c>blocker-diagnosis</c> sub-workflow via <see cref="EmitBlockerEventActivity"/>.
/// Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention
/// (<c>CLAUDE.md</c>) and mirrors the sibling event catalogues
/// (<see cref="Tamma.Activities.ADL.BranchEvents"/>,
/// <see cref="Tamma.Activities.ADL.TddDebugEvents"/>).
///
/// <para>The blocker-diagnosis workflow is a progressive-escalation human-in-the-loop
/// ladder (Hint → Guidance → Assistance → Escalation). Each rung is an auditable
/// intervention so time-travel debugging and the Epic-32 benchmarking/learning loop can
/// reconstruct <i>why</i> a junior was stuck, which level resolved it (if any), and
/// whether a wait expired without progress. Without these events the ladder's decisions
/// are invisible to the audit trail (the headline AC9 gap).</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="Tamma.Activities.ADL.EmitBranchEventActivity"/> and
/// <see cref="Tamma.Activities.ADL.EmitPrEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a directly
/// injected <c>IEventRepository</c> would be inert and silently drop every event). The
/// drain resolves the tenant from the workflow scope, and each event carries a
/// <c>tenantId</c> tag (SaaS) so per-tenant perf/action data stays tenant-scoped
/// (Epic 32).</para>
///
/// <list type="bullet">
///   <item><description><c>BLOCKER.DIAGNOSED.SUCCESS</c> / <c>.FAILED</c> — the
///     classifier produced (or failed to produce) a blocker type + severity.</description></item>
///   <item><description><c>BLOCKER.RESOLUTION_ATTEMPTED</c> — a resolution level
///     (Hint/Guidance/Assistance) was applied (carries level + attempt#).</description></item>
///   <item><description><c>BLOCKER.PROGRESS_DETECTED</c> — the junior made progress
///     after an intervention (carries progress type/details).</description></item>
///   <item><description><c>BLOCKER.PROGRESS_TIMED_OUT</c> — a level's wait expired with
///     no progress; the ladder advanced to the next rung.</description></item>
///   <item><description><c>BLOCKER.ESCALATED</c> — the ladder exhausted automated
///     resolution and notified a senior (carries channel + severity).</description></item>
///   <item><description><c>BLOCKER.RESOLVED</c> — terminal: the blocker was resolved
///     (progress detected at some level, or a senior resolved an escalation).</description></item>
///   <item><description><c>BLOCKER.TIMED_OUT</c> — terminal: the escalation SLA expired
///     with no senior response. LOUD (error-status) — a never-answered escalation is a
///     distinct, auditable terminal, NEVER a silent "Escalated" false success.</description></item>
/// </list>
/// </summary>
public static class BlockerEvents
{
    public const string DiagnosedSuccess = "BLOCKER.DIAGNOSED.SUCCESS";

    // FOLLOW-UP: BLOCKER.DIAGNOSED.FAILED is not emitted on any graph edge today — the graph
    // only ever emits DiagnosedSuccess after ClassifyBlockerActivity (which never throws: it
    // always falls back to a rule-based classification). It is retained as the LOUD,
    // fail-closed default for EmitBlockerEventActivity when an emit is reached with no
    // EventType (so a mis-wired emit surfaces as an error row, not a false success), and is
    // the slot a real diagnosis-failure path would emit once classification is allowed to hard
    // fail. Kept deliberately rather than removed; no fabricated failure path is added here.
    public const string DiagnosedFailed = "BLOCKER.DIAGNOSED.FAILED";
    public const string ResolutionAttempted = "BLOCKER.RESOLUTION_ATTEMPTED";
    public const string ProgressDetected = "BLOCKER.PROGRESS_DETECTED";
    public const string ProgressTimedOut = "BLOCKER.PROGRESS_TIMED_OUT";
    public const string Escalated = "BLOCKER.ESCALATED";
    public const string Resolved = "BLOCKER.RESOLVED";
    public const string TimedOut = "BLOCKER.TIMED_OUT";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (blocker events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="Tamma.Activities.ADL.BranchEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: an escalation SLA expiry (<see cref="TimedOut"/>) and a failed
    /// diagnosis (<see cref="DiagnosedFailed"/>) are LOUD (error-status) audit rows;
    /// every other transition (diagnosed, resolution attempted, progress detected, a
    /// per-level progress timeout that simply advances the ladder, escalated, resolved)
    /// is a normal (success-status) row. A per-level <see cref="ProgressTimedOut"/> is
    /// success-status because it is an expected ladder transition (the next rung runs) —
    /// only the FINAL escalation timeout is error-status. Keeps a degraded/expired
    /// terminal from ever being recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        TimedOut => "error",
        DiagnosedFailed => "error",
        _ => "success",
    };
}
