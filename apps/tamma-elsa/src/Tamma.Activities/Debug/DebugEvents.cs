namespace Tamma.Activities.Debug;

/// <summary>
/// Completeness audit 2026-06-22 (<c>Debugging.md</c> §Missing #8) — central catalogue
/// of the <c>DEBUG.*</c> DCB event types emitted by the built-out <c>debugging</c>
/// sub-workflow via <see cref="EmitDebugEventActivity"/>. Type pattern follows the
/// platform's <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors
/// the sibling event catalogues (<see cref="Tamma.Activities.Testing.TestingEvents"/>,
/// <see cref="Tamma.Activities.ADL.TddDebugEvents"/>).
///
/// <para>The core completeness gap was a genuine hypothesis-driven diagnose→fix→verify
/// loop with an iteration cap, a BugInvestigation regression branch and an escalation
/// report — but ZERO audit events anywhere, violating <c>CLAUDE.md</c> ("every operation
/// must emit events") and Story 7-1I's audit-trail intent. Without these the workflow's
/// diagnosis, hypothesis selection, fix attempts and terminal resolved/escalated
/// decisions are invisible to the audit trail and time-travel debugging.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="Tamma.Activities.Testing.EmitTestingEventActivity"/> uses. No activity
/// holds a DB / repository dependency of its own (none is registered in the Elsa
/// engine — a directly-injected <c>IEventRepository</c> would be inert and silently drop
/// every event). The drain resolves the tenant from the workflow scope, so a SaaS
/// caller's debug events carry the tenant tag.</para>
/// </summary>
public static class DebugEvents
{
    public const string SessionStarted = "DEBUG.SESSION.STARTED";
    public const string DiagnosisSuccess = "DEBUG.DIAGNOSIS.SUCCESS";
    public const string DiagnosisFailed = "DEBUG.DIAGNOSIS.FAILED";
    public const string HypothesisSelected = "DEBUG.HYPOTHESIS.SELECTED";
    public const string FixAttempted = "DEBUG.FIX.ATTEMPTED";
    public const string TestsPassed = "DEBUG.TESTS.PASSED";
    public const string TestsFailed = "DEBUG.TESTS.FAILED";
    public const string RegressionInvalid = "DEBUG.REGRESSION_TEST.INVALID";
    public const string ResolvedSuccess = "DEBUG.RESOLVED.SUCCESS";
    public const string Escalated = "DEBUG.ESCALATED.FAILED";

    /// <summary>
    /// The terminal <c>reason</c> values the workflow stamps on its escalation so the
    /// caller / audit trail can distinguish WHY the debug loop escalated. Never an empty
    /// string — an escalation always carries a reason (no false success, no silent
    /// failure). Mirrors <c>TestingEvents</c>'s escalation-reason pattern.
    /// </summary>
    public const string ReasonNoHypothesis = "no-hypothesis-selected";
    public const string ReasonMaxIterations = "max-iterations-reached";
    public const string ReasonRegressionInvalid = "regression-test-did-not-reproduce-bug";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs.
    /// Returns <c>null</c> for empty / single-user / unparseable values (debug events in
    /// single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.Testing.TestingEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a diagnosis failure, a test failure, an invalid regression test
    /// and a terminal escalation are LOUD (error-status) audit rows; every other
    /// transition (session started, diagnosis success, hypothesis selected, fix attempted,
    /// tests passed, terminal resolved) is a normal (success-status) row. Keeps a degraded
    /// / failed terminal from ever being recorded as a false success. The
    /// <c>DEBUG.FIX.ATTEMPTED</c> row carries a <c>success</c> flag in its data payload so
    /// a failed fix is still a non-error transition (the loop legitimately continues), but
    /// the failure is visible.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        DiagnosisFailed => "error",
        TestsFailed => "error",
        RegressionInvalid => "error",
        Escalated => "error",
        _ => "success",
    };
}
