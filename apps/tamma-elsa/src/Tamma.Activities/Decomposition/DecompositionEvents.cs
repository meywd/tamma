namespace Tamma.Activities.Decomposition;

/// <summary>
/// Story 2.14 (Issue Decomposition) — central catalogue of the <c>DECOMPOSITION.*</c> DCB event
/// types emitted by the <c>issue-decomposition</c> sub-workflow via
/// <see cref="EmitDecompositionEventActivity"/>. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors the sibling event
/// catalogues (<see cref="Tamma.Activities.Research.ResearchEvents"/>,
/// <see cref="Tamma.Activities.Ambiguity.AmbiguityEvents"/>).
///
/// <para>The decomposition workflow breaks a complex issue / requirement into an ordered set of
/// smaller, implementable sub-tasks (with rationale, sizing and declared dependencies) by asking
/// the LLM via the mediated <c>llm-call</c> path — the engine holds no LLM credential. Each
/// transition — start, context gathered, and the terminal outcome — is an auditable step so
/// time-travel debugging and the Epic-32 learning loop can reconstruct WHAT issue was decomposed,
/// HOW it was broken down, and HOW MANY sub-tasks resulted (Story 2.14 AC6 traceability + AC8
/// learning). Without these events the decomposition is invisible to the audit trail.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern the sibling
/// catalogues use. No activity holds a DB / repository dependency of its own; the drain resolves
/// the tenant from the workflow scope and each event carries a <c>tenantId</c> tag so per-tenant
/// data stays tenant-scoped.</para>
///
/// <list type="bullet">
///   <item><description><c>DECOMPOSITION.STARTED</c> — the workflow began analysing the issue for
///     decomposition.</description></item>
///   <item><description><c>DECOMPOSITION.CONTEXT_GATHERED</c> — the codebase / prior-art context
///     was gathered (via the reused <c>context-gathering</c> sub-workflow) to inform the
///     breakdown.</description></item>
///   <item><description><c>DECOMPOSITION.COMPLETED</c> — terminal success: the LLM returned a valid
///     decomposition; the sub-task set (with its count) is recorded.</description></item>
///   <item><description><c>DECOMPOSITION.FAILED</c> — LOUD (error-status): the decomposition
///     <c>llm-call</c> failed or returned unparseable / empty output; the workflow fails closed
///     rather than emitting a fabricated breakdown.</description></item>
/// </list>
/// </summary>
public static class DecompositionEvents
{
    public const string Started = "DECOMPOSITION.STARTED";
    public const string ContextGathered = "DECOMPOSITION.CONTEXT_GATHERED";
    public const string Completed = "DECOMPOSITION.COMPLETED";

    // LOUD (error-status) terminal — a fabricated / degraded breakdown must never be recorded as a
    // false success.
    public const string Failed = "DECOMPOSITION.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs. Returns
    /// <c>null</c> for empty / single-user / unparseable values (decomposition events in
    /// single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.Research.ResearchEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed decomposition is a LOUD (error-status) audit row; the start
    /// transition is an informational (started) row; every other transition (context gathered,
    /// completed) is a normal (success-status) row. Keeps a degraded terminal from ever being
    /// recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        Failed => "error",
        Started => "started",
        _ => "success",
    };
}
