namespace Tamma.Activities.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageItemCycle.md</c> #3) / Story 26-1 AC9 —
/// central catalogue of the <b>cycle-scoped</b> <c>TRIAGE.ISSUE.*</c> DCB event types
/// emitted by the <c>triage-item-cycle</c> workflow. Type pattern follows the
/// platform's <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors
/// the sibling per-stage catalogues (<see cref="TriageContextEvents"/>,
/// <see cref="TriageEvents"/>, <see cref="TriagePoDecisionEvents"/>).
///
/// <para>The per-item cycle is the <b>unit of audit</b>: "we triaged item X →
/// outcome Y". Before this build-out the cycle emitted only the leaf
/// <c>TRIAGE.APPLY.RESULT.*</c> events — the stage sub-workflows each emit their own
/// <c>TRIAGE.{CONTEXT,PANEL,PO_DECISION}.*</c> events, but there was NO event for the
/// cycle as a whole, so the audit trail could not answer "was item X triaged, skipped,
/// or did it fail?" without reconstructing it from the leaf events. These cycle events
/// make the outcome a single loud audit row.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern the
/// per-stage emitters use. No activity holds a DB / repository dependency of its own
/// (none is registered in the Elsa engine — a directly-injected <c>IEventRepository</c>
/// would be inert and silently drop every event). The drain resolves the tenant from
/// the workflow's <c>TenantId</c> variable, so a SaaS caller's cycle events carry the
/// tenant tag.</para>
///
/// <list type="bullet">
///   <item><description><c>TRIAGE.ISSUE.STARTED</c> — the cycle began processing an
///     item (tags carry repository + item key + item source/type).</description></item>
///   <item><description><c>TRIAGE.ISSUE.COMPLETED</c> — the item was fully triaged and
///     labels/comment were applied (data carries type/priority/automation +
///     decisionStatus). Success.</description></item>
///   <item><description><c>TRIAGE.ISSUE.SKIPPED</c> — the cycle stopped before applying
///     because a stage reported a non-applying-but-not-faulted signal (context could not
///     be gathered, or the panel fell below quorum). A degraded — not faulted — outcome;
///     loud (warning-status) so the audit trail records "we deliberately did not label
///     this", never a silent no-op.</description></item>
///   <item><description><c>TRIAGE.ISSUE.FAILED</c> — a sub-workflow faulted, or the PO
///     produced no usable decision, or the apply step failed. Loud (error-status). NO
///     labels are applied off a fabricated/empty decision — the no-false-success /
///     no-empty-fallback rule made explicit at the cycle level.</description></item>
/// </list>
/// </summary>
public static class TriageCycleEvents
{
    public const string Started = "TRIAGE.ISSUE.STARTED";
    public const string Completed = "TRIAGE.ISSUE.COMPLETED";
    public const string Skipped = "TRIAGE.ISSUE.SKIPPED";
    public const string Failed = "TRIAGE.ISSUE.FAILED";

    /// <summary>
    /// The per-item <c>outcome</c> values surfaced on the cycle's <c>itemResult</c>
    /// output (<see cref="TriageItemCycle.md"/> #5) so the fire-and-forget parent can
    /// report <c>{ triaged, failed, skipped }</c> instead of a blanket success.
    /// </summary>
    public const string OutcomeTriaged = "triaged";
    public const string OutcomeSkipped = "skipped";
    public const string OutcomeFailed = "failed";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values (cycle
    /// events in single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="TriageEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed cycle is a loud (error-status) audit row, a skipped
    /// cycle is a warning-status row, a started/completed cycle is success. Mirrors the
    /// per-stage catalogues — a degraded/failed outcome is never recorded as a false
    /// success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        Failed => "error",
        Skipped => "warning",
        _ => "success",
    };
}
