namespace Tamma.Activities.Design;

/// <summary>
/// Story 3.7 (Design Proposal Workflow) — central catalogue of the <c>DESIGN.*</c> DCB
/// event types emitted by the <c>design-proposal</c> sub-workflow via
/// <see cref="EmitDesignEventActivity"/>. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors the sibling
/// event catalogues (<see cref="Tamma.Activities.Clarify.ClarifyEvents"/>,
/// <see cref="Tamma.Activities.Blocker.BlockerEvents"/>).
///
/// <para>The design-proposal workflow turns a complex requirement into a reviewed
/// technical design: it asks the LLM (via the mediated <c>llm-call</c> path — the engine
/// holds no LLM credential, TAMMA001) to generate a design proposal with alternatives +
/// trade-off analysis, DELIVERS it to the issue / reviewer, SUSPENDS on a bookmark
/// awaiting a human approve/reject decision, then RESUMES to finalise (approved →
/// hand-off, rejected → feedback captured). Each transition is an auditable step so
/// time-travel debugging and the Epic-32 learning loop can reconstruct WHAT was proposed,
/// WHICH alternatives were weighed, and HOW the design decision was made — satisfying the
/// Story-3.7 ACs "System tracks design decisions and maintains decision audit trail" +
/// "Proposals are versioned and stored for future reference and learning".</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="Tamma.Activities.Clarify.EmitClarifyEventActivity"/> uses. No activity holds
/// a DB / repository dependency of its own; the drain resolves the tenant from the
/// workflow scope and each event carries a <c>tenantId</c> tag so per-tenant data stays
/// tenant-scoped.</para>
///
/// <list type="bullet">
///   <item><description><c>DESIGN.PROPOSAL.GENERATED</c> — the LLM produced a design
///     proposal (summary + alternatives + trade-off analysis) for the requirement.</description></item>
///   <item><description><c>DESIGN.PROPOSAL.DELIVERED</c> — the proposal was posted to the
///     issue / surfaced to the reviewer (carries channel).</description></item>
///   <item><description><c>DESIGN.PROPOSAL.APPROVED</c> — terminal success: a human
///     reviewer approved the design; it may proceed to implementation.</description></item>
///   <item><description><c>DESIGN.PROPOSAL.REJECTED</c> — a human reviewer rejected the
///     design (feedback captured). A real decision, NOT an error — recorded as a normal
///     audit row so the rejection + its feedback feed the learning loop.</description></item>
///   <item><description><c>DESIGN.PROPOSAL.FAILED</c> — LOUD (error-status): the
///     generation <c>llm-call</c> failed or returned unparseable output; the workflow
///     fails closed rather than proceeding with a fabricated design.</description></item>
///   <item><description><c>DESIGN.REVIEW.TIMED_OUT</c> — LOUD (error-status): the review
///     SLA expired with no human decision. A distinct, auditable terminal, NEVER a silent
///     false approval.</description></item>
/// </list>
/// </summary>
public static class DesignEvents
{
    public const string ProposalGenerated = "DESIGN.PROPOSAL.GENERATED";
    public const string ProposalDelivered = "DESIGN.PROPOSAL.DELIVERED";
    public const string ProposalApproved = "DESIGN.PROPOSAL.APPROVED";
    public const string ProposalRejected = "DESIGN.PROPOSAL.REJECTED";

    // LOUD (error-status) terminals — a fabricated / degraded / expired outcome must never
    // be recorded as a false success.
    public const string ProposalFailed = "DESIGN.PROPOSAL.FAILED";
    public const string ReviewTimedOut = "DESIGN.REVIEW.TIMED_OUT";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs.
    /// Returns <c>null</c> for empty / single-user / unparseable values (design events in
    /// single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.Clarify.ClarifyEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed generation and a review SLA expiry are LOUD
    /// (error-status) audit rows; every other transition (generated, delivered, approved,
    /// rejected) is a normal (success-status) row. A REJECTED proposal is a legitimate
    /// human decision, not an error. Keeps a degraded/expired terminal from ever being
    /// recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        ProposalFailed => "error",
        ReviewTimedOut => "error",
        _ => "success",
    };
}
