namespace Tamma.Activities.Research;

/// <summary>
/// Story 3.4 (Research Workflow) — central catalogue of the <c>RESEARCH.*</c> DCB
/// event types emitted by the <c>research</c> sub-workflow via
/// <see cref="EmitResearchEventActivity"/>. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors the sibling
/// event catalogues (<see cref="Tamma.Activities.Clarify.ClarifyEvents"/>,
/// <see cref="Tamma.Activities.Blocker.BlockerEvents"/>).
///
/// <para>The research workflow is triggered when ambiguity is detected in a
/// requirement: it investigates the codebase / prior art (reusing the
/// <c>context-gathering</c> sub-workflow) and asks the LLM (via the mediated
/// <c>llm-call</c> path — the engine holds no LLM credential) to synthesize the gathered
/// context into a ranked, confidence-scored research report. Each transition is an
/// auditable step so time-travel debugging and the Epic-32 learning loop can reconstruct
/// WHAT was researched, WHICH sources were investigated, and HOW confident the findings
/// are (Story 3.4 ACs "Research results are stored and linked to original issues for
/// traceability" + "System generates research reports with citations and confidence
/// scores"). Without these events the research is invisible to the audit trail.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern the
/// sibling catalogues use. No activity holds a DB / repository dependency of its own; the
/// drain resolves the tenant from the workflow scope and each event carries a
/// <c>tenantId</c> tag so per-tenant data stays tenant-scoped.</para>
///
/// <list type="bullet">
///   <item><description><c>RESEARCH.STARTED</c> — the research workflow began
///     investigating the topic / ambiguous requirement.</description></item>
///   <item><description><c>RESEARCH.CONTEXT_GATHERED</c> — the codebase / prior-art
///     context was gathered (via the reused <c>context-gathering</c> sub-workflow) and is
///     ready for synthesis.</description></item>
///   <item><description><c>RESEARCH.COMPLETED</c> — terminal success: the LLM synthesized
///     a non-empty, ranked, confidence-scored research report.</description></item>
///   <item><description><c>RESEARCH.FAILED</c> — LOUD (error-status): the synthesis
///     <c>llm-call</c> failed or returned unparseable output; the workflow fails closed
///     rather than emitting a fabricated report.</description></item>
/// </list>
/// </summary>
public static class ResearchEvents
{
    public const string Started = "RESEARCH.STARTED";
    public const string ContextGathered = "RESEARCH.CONTEXT_GATHERED";
    public const string Completed = "RESEARCH.COMPLETED";

    // LOUD (error-status) terminal — a fabricated / degraded outcome must never be
    // recorded as a false success.
    public const string Failed = "RESEARCH.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values (research
    /// events in single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.Clarify.ClarifyEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed synthesis is a LOUD (error-status) audit row; the
    /// start transition is an informational (started) row; every other transition
    /// (context gathered, completed) is a normal (success-status) row. Keeps a degraded
    /// terminal from ever being recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        Failed => "error",
        Started => "started",
        _ => "success",
    };
}
