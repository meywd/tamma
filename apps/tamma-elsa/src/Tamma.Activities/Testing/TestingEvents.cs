namespace Tamma.Activities.Testing;

/// <summary>
/// Completeness audit 2026-06-22 (<c>Testing.md</c> §Missing #3, Build-out Phase 1 step 4)
/// — central catalogue of the <c>TEST.*</c> / <c>GATE.*</c> DCB event types emitted by
/// the built-out <c>testing-pipeline</c> workflow via <see cref="EmitTestingEventActivity"/>.
/// Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention
/// (<c>CLAUDE.md</c>) and mirrors the sibling event catalogue
/// (<see cref="Tamma.Activities.ADL.TddDebugEvents"/>).
///
/// <para>The core completeness gap was a real multi-branch pipeline with a bookmark CI
/// wait, an auto-fix loop and skill-level thresholds — but ZERO audit events anywhere,
/// violating <c>CLAUDE.md</c> ("every operation must emit events"), Story 2-5 AC7
/// ("logged to event trail") and Story 7-7 AC6 (improvement tracking). Without these the
/// pipeline's quality-gate decisions, fix commits and terminal pass/fail/escalation are
/// invisible to the audit trail and time-travel debugging.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="Tamma.Activities.ADL.EmitTddDebugEventActivity"/> uses. No activity holds a
/// DB / repository dependency of its own (none is registered in the Elsa engine — a
/// directly-injected <c>IEventRepository</c> would be inert and silently drop every
/// event). The drain resolves the tenant from the workflow scope, so a SaaS caller's
/// testing events carry the tenant tag.</para>
///
/// <list type="bullet">
///   <item><description><c>TEST.CI_TRIGGERED.SUCCESS</c> / <c>.FAILED</c> — a CI run was
///     requested; FAILED is a LOUD (error) row taken when the trigger itself failed (no
///     run id → guaranteed hang downstream), routed to escalation, never a silent
///     proceed-into-a-dead-wait.</description></item>
///   <item><description><c>TEST.RESULTS_RECEIVED</c> — CI results arrived via the
///     bookmark resume (carries runId / build-passed / failed-test count).</description></item>
///   <item><description><c>TEST.CI_TIMED_OUT</c> — the bookmark wait hit its
///     <c>TimeoutMinutes</c> deadline before CI reported. LOUD (error) — the workflow
///     takes a deterministic failure edge instead of suspending forever.</description></item>
///   <item><description><c>GATE.EVALUATED</c> — a quality-gate evaluation completed
///     (carries outcome / score / skillLevel).</description></item>
///   <item><description><c>GATE.AUTOFIX_COMMITTED</c> — an auto-fix commit landed real
///     changes (carries attempt / filesChanged).</description></item>
///   <item><description><c>GATE.AUTOFIX_NOOP</c> — the fix generation produced NO file
///     changes; treated as a non-fix and routed to escalation rather than re-triggering CI
///     and pretending progress. LOUD (error) — closes the false-success hole.</description></item>
///   <item><description><c>GATE.PASSED</c> — a terminal pass (all gates green on a real
///     CI result).</description></item>
///   <item><description><c>GATE.FAILED</c> — a terminal fail. LOUD (error).</description></item>
///   <item><description><c>GATE.ESCALATED</c> — a terminal escalation to human review
///     (critical / retry-exhausted / ci-timeout / ci-trigger-failed / autofix-noop). LOUD
///     (error) — the mandatory escalation after the bounded retry budget, never a silent
///     give-up.</description></item>
/// </list>
/// </summary>
public static class TestingEvents
{
    public const string CiTriggeredSuccess = "TEST.CI_TRIGGERED.SUCCESS";
    public const string CiTriggeredFailed = "TEST.CI_TRIGGERED.FAILED";
    public const string ResultsReceived = "TEST.RESULTS_RECEIVED.SUCCESS";
    public const string CiTimedOut = "TEST.CI_TIMED_OUT.FAILED";
    public const string GateEvaluated = "GATE.EVALUATED.SUCCESS";
    public const string AutofixCommitted = "GATE.AUTOFIX_COMMITTED.SUCCESS";
    public const string AutofixNoop = "GATE.AUTOFIX_NOOP.FAILED";
    public const string GatePassed = "GATE.PASSED.SUCCESS";
    public const string GateFailed = "GATE.FAILED.FAILED";
    public const string GateEscalated = "GATE.ESCALATED.FAILED";

    /// <summary>
    /// The terminal <c>escalationReason</c> values the workflow stamps on its escalation
    /// output so the caller / audit trail can distinguish WHY the pipeline escalated to a
    /// human gate. Never an empty string — an escalation always carries a reason (no false
    /// success, no silent failure). Mirrors <c>TddDebugEvents</c>'s finish-reason pattern.
    /// </summary>
    public const string ReasonCritical = "critical-quality-failure";
    public const string ReasonRetryExhausted = "retry-budget-exhausted";
    public const string ReasonCiTimeout = "ci-timeout";
    public const string ReasonCiTriggerFailed = "ci-trigger-failed";
    public const string ReasonAutofixNoop = "autofix-no-op";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs.
    /// Returns <c>null</c> for empty / single-user / unparseable values (testing events in
    /// single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.ADL.TddDebugEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a CI trigger failure, a CI timeout, an auto-fix no-op, a terminal
    /// fail and a terminal escalation are LOUD (error-status) audit rows; every other
    /// transition (CI triggered, results received, gate evaluated, auto-fix committed,
    /// terminal pass) is a normal (success-status) row. Keeps a degraded / failed terminal
    /// from ever being recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        CiTriggeredFailed => "error",
        CiTimedOut => "error",
        AutofixNoop => "error",
        GateFailed => "error",
        GateEscalated => "error",
        _ => "success",
    };
}
