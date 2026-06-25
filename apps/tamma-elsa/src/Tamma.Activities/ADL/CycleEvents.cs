namespace Tamma.Activities.ADL;

/// <summary>
/// Completeness audit 2026-06-22 (<c>SingleIssueCycle.md</c> §Missing #1/§Ordered
/// build-out Phase A) — central catalogue of the cycle-scoped <c>CYCLE.*</c> DCB event
/// types emitted by the built-out <c>single-issue-cycle</c> workflow (the per-issue
/// "roundabout") via <see cref="EmitCycleEventActivity"/>. Type pattern follows the
/// platform's <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors
/// the sibling catalogues (<see cref="TddDebugEvents"/>, <see cref="BranchEvents"/>,
/// <see cref="PrEvents"/>).
///
/// <para>The individual composed activities in the cycle (validate / wait / report /
/// branch / PR / merge / deploy) already auto-emit their own per-activity Start/Success/
/// Failure events through <c>TammaActivity</c>. These <c>CYCLE.*</c> events sit at the
/// <i>orchestration boundaries</i> so time-travel debugging can reconstruct the cycle's
/// own lifecycle independently of any one step: when the roundabout started, which step
/// failed it (the loud fail-the-cycle sink), and whether it completed. Without a
/// cycle-scoped boundary event a faulted sub-workflow that the cycle routes to its error
/// sink would be invisible at the cycle granularity.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — no activity holds a DB
/// / repository dependency of its own (none is registered in the Elsa engine; a direct
/// <c>IEventRepository</c> would be inert). The drain resolves the tenant from the
/// workflow's <c>TenantId</c> variable, so a SaaS caller's cycle events carry the tenant
/// tag (single-user → platform-scope, tenant tag omitted).</para>
///
/// <list type="bullet">
///   <item><description><c>CYCLE.STARTED</c> — the roundabout began for a validated work
///     item (carries the issue number + repository).</description></item>
///   <item><description><c>CYCLE.STEP_FAILED</c> — an awaited sub-workflow / step produced
///     an absent / invalid critical output and the cycle routed it to the loud
///     fail-the-cycle sink (carries <c>stepId</c> + the underlying detail). Loud
///     (error-status) — NEVER a silently swallowed COMPLETED.</description></item>
///   <item><description><c>CYCLE.COMPLETED</c> — the cycle reached a successful terminal
///     (PR merged + deployment pipeline result inspected).</description></item>
///   <item><description><c>CYCLE.FAILED</c> — the cycle reached its loud failure terminal
///     (any fail-the-cycle path). Loud (error-status).</description></item>
/// </list>
/// </summary>
public static class CycleEvents
{
    public const string Started = "CYCLE.STARTED";
    public const string StepFailed = "CYCLE.STEP_FAILED";
    public const string Completed = "CYCLE.COMPLETED";
    public const string Failed = "CYCLE.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (cycle events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="TddDebugEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a step failure and a cycle failure are LOUD (error-status)
    /// audit rows; cycle started / completed are normal (success-status) rows. Keeps a
    /// failed cycle from ever being recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        StepFailed => "error",
        Failed => "error",
        _ => "success",
    };
}
