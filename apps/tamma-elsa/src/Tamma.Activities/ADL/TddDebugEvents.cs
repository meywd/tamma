namespace Tamma.Activities.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TddWithDebugRetry.md</c> §Missing #3) — central
/// catalogue of the <c>TDD_DEBUG.*</c> DCB event types emitted by the built-out
/// <c>tdd-with-debug-retry</c> workflow via <see cref="EmitTddDebugEventActivity"/>.
/// Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention
/// (<c>CLAUDE.md</c>) and mirrors the sibling event catalogues
/// (<see cref="BranchEvents"/>, <see cref="PrEvents"/>, <see cref="TriageContextEvents"/>).
///
/// <para>The <c>tdd-with-debug-retry</c> workflow is a pivot-clean <i>orchestrator</i>:
/// it dispatches the <c>tdd-cycle</c> red-green-refactor sub-workflow inside a
/// graph-enforced debug-retry loop (bounded by <c>maxRetries</c>), and on a TDD
/// failure dispatches the <c>debugging</c> sub-workflow before re-running the cycle.
/// Each loop boundary is an auditable event so time-travel debugging can reconstruct
/// <i>why</i> an issue cycle stalled in TDD — whether the cycle converged, how many
/// debug attempts were burned, whether the debugger escalated, and whether the loop
/// exhausted its retry budget. Without these the orchestrator's retry decisions are
/// invisible to the audit trail.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitBranchEventActivity"/> and <see cref="EmitPrEventActivity"/>
/// use. No activity holds a DB / repository dependency of its own (none is registered
/// in the Elsa engine — a directly-injected <c>IEventRepository</c> would be inert and
/// silently drop every event). The drain resolves the tenant from the workflow's
/// <c>TenantId</c> variable, so a SaaS caller's TDD events carry the tenant tag.</para>
///
/// <list type="bullet">
///   <item><description><c>TDD_DEBUG.CYCLE.STARTED</c> — a <c>tdd-cycle</c> dispatch
///     is about to run (carries the current attempt number).</description></item>
///   <item><description><c>TDD_DEBUG.CYCLE.PASSED</c> — the dispatched cycle reported
///     <c>success=true</c> (the orchestrator finishes success).</description></item>
///   <item><description><c>TDD_DEBUG.CYCLE.FAILED</c> — the dispatched cycle reported
///     <c>success=false</c> (the orchestrator consults the retry guard).</description></item>
///   <item><description><c>TDD_DEBUG.DEBUG.ATTEMPTED</c> — within the retry budget,
///     the orchestrator dispatched the <c>debugging</c> sub-workflow.</description></item>
///   <item><description><c>TDD_DEBUG.DEBUGGER.ESCALATED</c> — the dispatched
///     <c>debugging</c> sub-workflow itself returned <c>success=false</c> (it could not
///     fix the failure and escalated). The orchestrator short-circuits to a LOUD
///     failure terminal instead of looping back and burning a retry on a
///     known-unfixable failure. Loud (error-status).</description></item>
///   <item><description><c>TDD_DEBUG.RETRY.EXHAUSTED</c> — the retry guard fired
///     <c>False</c> (the loop ran out of its <c>maxRetries</c> budget without the
///     cycle converging). Loud (error-status) — this is the explicit exhaustion
///     terminal, NEVER a silent success.</description></item>
///   <item><description><c>TDD_DEBUG.COMPLETED.SUCCESS</c> — the orchestrator finished
///     success (the cycle converged within the retry budget).</description></item>
/// </list>
/// </summary>
public static class TddDebugEvents
{
    public const string CycleStarted = "TDD_DEBUG.CYCLE.STARTED";
    public const string CyclePassed = "TDD_DEBUG.CYCLE.PASSED";
    public const string CycleFailed = "TDD_DEBUG.CYCLE.FAILED";
    public const string DebugAttempted = "TDD_DEBUG.DEBUG.ATTEMPTED";
    public const string DebuggerEscalated = "TDD_DEBUG.DEBUGGER.ESCALATED";
    public const string RetryExhausted = "TDD_DEBUG.RETRY.EXHAUSTED";
    public const string CompletedSuccess = "TDD_DEBUG.COMPLETED.SUCCESS";

    /// <summary>
    /// The <c>finishReason</c> values the orchestrator stamps on its failure output so
    /// the caller / audit trail can distinguish a genuine TDD non-convergence (the loop
    /// exhausted its retries) from a debugger crash/escalation. Mirrors
    /// <c>TddWorkflow</c>'s <c>finishReason</c> pattern. Never an empty string —
    /// failure always carries a reason (no false success, no silent failure).
    /// </summary>
    public const string ReasonNotConverged = "tdd-not-converged";
    public const string ReasonDebuggerEscalated = "debugger-escalated";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (TDD events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="BranchEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a retry-exhausted loop and a debugger escalation are LOUD
    /// (error-status) audit rows; every other transition (cycle started/passed/failed,
    /// debug attempted, completed-success) is a normal (success-status) row.
    /// A FAILED cycle is success-status because it is an expected, recoverable loop
    /// transition (the orchestrator retries it) — only the terminal exhaustion /
    /// escalation rows are error-status. Keeps a degraded/failed terminal from ever
    /// being recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        RetryExhausted => "error",
        DebuggerEscalated => "error",
        _ => "success",
    };
}
