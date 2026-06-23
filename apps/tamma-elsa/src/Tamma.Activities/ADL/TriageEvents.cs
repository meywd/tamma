namespace Tamma.Activities.ADL;

/// <summary>
/// Story 26-1 (AC9 intent) / triage-cluster audit P1 — central catalogue of the
/// <c>TRIAGE.PANEL.*</c> DCB event types emitted by the <c>triage-panel-review</c>
/// workflow. Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention (<c>CLAUDE.md</c>).
///
/// <para>The triage panel is a 4-role LLM panel (security / developer / devops /
/// tester) over one triage item. Each lifecycle transition of the panel is an
/// auditable event so the audit trail (and the downstream PO decision) can see a
/// degraded or failed panel rather than a silent false success.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitPrEventActivity"/> and
/// <see cref="EmitMergeApprovalEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a
/// directly-injected <c>IEventRepository</c> would be inert and silently drop
/// every panel event).</para>
///
/// <list type="bullet">
///   <item><description><c>TRIAGE.PANEL.STARTED</c> — the panel began reviewing an
///     item (tags carry repository + item number).</description></item>
///   <item><description><c>TRIAGE.PANEL.COMPLETED</c> — every panellist produced a
///     usable assessment (full, healthy panel).</description></item>
///   <item><description><c>TRIAGE.PANEL.PARTIAL</c> — at least one panellist failed
///     but the panel still met quorum. A degraded — not failed — result; loud
///     (warning-status) so the PO can down-weight it.</description></item>
///   <item><description><c>TRIAGE.PANEL.FAILED</c> — too few panellists produced a
///     usable assessment (below quorum). Loud (error-status). The panel reports
///     <c>panelStatus="failed"</c> so the parent cycle routes to a non-applying
///     terminal instead of labelling the item off a wholly-failed panel. This is
///     the no-false-success / no-empty-fallback rule made explicit: a failed panel
///     is NEVER coalesced to four <c>{}</c> reviews reported as a success.</description></item>
/// </list>
/// </summary>
public static class TriageEvents
{
    public const string PanelStarted = "TRIAGE.PANEL.STARTED";
    public const string PanelCompleted = "TRIAGE.PANEL.COMPLETED";
    public const string PanelPartial = "TRIAGE.PANEL.PARTIAL";
    public const string PanelFailed = "TRIAGE.PANEL.FAILED";

    /// <summary>Default quorum: at least this many panellists must produce a
    /// usable assessment for the panel to be considered usable (not failed).</summary>
    public const int DefaultQuorum = 2;

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (panel events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Failure-status check: a failed panel is a loud (error-status) audit row, a
    /// partial panel is a warning-status row, a completed panel is success.
    /// Mirrors <see cref="EmitMergeApprovalEventActivity.IsFailureEvent"/>'s intent
    /// of never recording a degraded/failed outcome as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        PanelFailed => "error",
        PanelPartial => "warning",
        _ => "success",
    };
}
