namespace Tamma.Activities.Ambiguity;

/// <summary>
/// Story 3.6 (Ambiguity Scoring) — central catalogue of the <c>AMBIGUITY.*</c> DCB event
/// types emitted by the <c>ambiguity-scoring</c> sub-workflow via
/// <see cref="EmitAmbiguityEventActivity"/>. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors the sibling event
/// catalogues (<see cref="Tamma.Activities.Research.ResearchEvents"/>,
/// <see cref="Tamma.Activities.Clarify.ClarifyEvents"/>).
///
/// <para>The scoring workflow quantifies how ambiguous / underspecified a requirement is
/// (a 0..1 score + a typed, itemised breakdown) by asking the LLM via the mediated
/// <c>llm-call</c> path — the engine holds no LLM credential. The score is then compared to a
/// caller-supplied threshold: above it, the requirement is routed to the sibling
/// <c>ClarifyingQuestionsWorkflow</c> (Story 3.5) before implementation proceeds; below it, the
/// requirement proceeds as-is. Each transition — start, scored, and the threshold DECISION —
/// is an auditable step so time-travel debugging and the Epic-32 learning loop can reconstruct
/// WHAT was scored, HOW ambiguous it was, and WHETHER clarification was triggered (Story 3.6
/// ACs). Without these events the scoring decision is invisible to the audit trail.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern the sibling
/// catalogues use. No activity holds a DB / repository dependency of its own; the drain
/// resolves the tenant from the workflow scope and each event carries a <c>tenantId</c> tag so
/// per-tenant data stays tenant-scoped.</para>
///
/// <list type="bullet">
///   <item><description><c>AMBIGUITY.STARTED</c> — the scorer began analysing the
///     requirement.</description></item>
///   <item><description><c>AMBIGUITY.SCORED</c> — terminal success: the LLM returned a valid
///     score + rationale; the assessment is recorded.</description></item>
///   <item><description><c>AMBIGUITY.CLARIFICATION_TRIGGERED</c> — threshold DECISION: the score
///     met/exceeded the threshold, so clarification is triggered before proceeding (AC6).</description></item>
///   <item><description><c>AMBIGUITY.BELOW_THRESHOLD</c> — threshold DECISION: the score was
///     below the threshold, so the requirement proceeds as-is (no clarification).</description></item>
///   <item><description><c>AMBIGUITY.FAILED</c> — LOUD (error-status): the scoring
///     <c>llm-call</c> failed or returned unparseable / out-of-range output; the workflow fails
///     closed rather than emitting a fabricated score.</description></item>
/// </list>
/// </summary>
public static class AmbiguityEvents
{
    public const string Started = "AMBIGUITY.STARTED";
    public const string Scored = "AMBIGUITY.SCORED";

    // Threshold-decision transitions (AC6). Both are normal (success-status) audit rows —
    // the decision itself is not an error; only a failed scoring is.
    public const string ClarificationTriggered = "AMBIGUITY.CLARIFICATION_TRIGGERED";
    public const string BelowThreshold = "AMBIGUITY.BELOW_THRESHOLD";

    // LOUD (error-status) terminal — a fabricated / degraded score must never be recorded as a
    // false success.
    public const string Failed = "AMBIGUITY.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs.
    /// Returns <c>null</c> for empty / single-user / unparseable values (ambiguity events in
    /// single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.Research.ResearchEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed scoring is a LOUD (error-status) audit row; the start
    /// transition is an informational (started) row; every other transition (scored + both
    /// threshold decisions) is a normal (success-status) row. Keeps a degraded terminal from
    /// ever being recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        Failed => "error",
        Started => "started",
        _ => "success",
    };
}
