namespace Tamma.Activities.Review;

/// <summary>
/// Completeness audit 2026-06-22 (<c>CodeReview.md</c> §Missing #8, Story 7-1D AC10) —
/// central catalogue of the <c>CODE_REVIEW.*</c> DCB event types emitted by the
/// <c>code-review</c> sub-workflow via <see cref="EmitCodeReviewEventActivity"/>.
/// Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention
/// (<c>CLAUDE.md</c>) and mirrors the sibling event catalogues
/// (<see cref="Tamma.Activities.Blocker.BlockerEvents"/>,
/// <see cref="Tamma.Activities.ADL.BranchEvents"/>).
///
/// <para>The code-review workflow drives the full PR lifecycle (create → request review →
/// monitor → guide fixes → re-review → merge / escalate). Each milestone is an auditable
/// transition so time-travel debugging and the Epic-32 benchmarking/learning loop can
/// reconstruct <i>how</i> a PR moved from creation to merge (or escalation), how many fix
/// iterations it took, and whether a wait expired. The pre-existing
/// <c>MentorshipEvent</c> rows are RETAINED in addition — they feed the mentorship state
/// machine; these DCB events feed the platform audit/time-travel stream.</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine event
/// drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="Tamma.Activities.ADL.EmitBranchEventActivity"/> and
/// <see cref="Tamma.Activities.Blocker.EmitBlockerEventActivity"/> use. No activity holds
/// a DB / repository dependency of its own (none is registered in the Elsa engine — a
/// directly injected <c>IEventRepository</c> would be inert and silently drop every
/// event). The drain resolves the tenant from the workflow scope, and each event carries a
/// <c>tenantId</c> tag (SaaS) so per-tenant perf/action data stays tenant-scoped
/// (Epic 32).</para>
///
/// <list type="bullet">
///   <item><description><c>CODE_REVIEW.PR_CREATED.SUCCESS</c> / <c>.FAILED</c> — the PR was
///     created (or creation failed and the run terminates).</description></item>
///   <item><description><c>CODE_REVIEW.GUIDANCE_DELIVERED.SUCCESS</c> / <c>.FAILED</c> — fix
///     guidance was generated (via mediated LLM) and delivered to the junior (or guidance
///     generation failed and the run escalates).</description></item>
///   <item><description><c>CODE_REVIEW.ITERATION.STARTED</c> — a new fix iteration began
///     (carries the iteration number).</description></item>
///   <item><description><c>CODE_REVIEW.MERGED.SUCCESS</c> / <c>.FAILED</c> — the PR was
///     merged (carries the merge sha + strategy) or merge failed irrecoverably.</description></item>
///   <item><description><c>CODE_REVIEW.ESCALATED</c> — Story 4-6: the review was escalated
///     to a senior. Emitted at the RAISE point (when <see cref="EscalateReviewActivity"/>
///     suspends awaiting the senior) so an escalation is auditable the moment it is
///     triggered — regardless of whether the senior later resolves, rejects, or the SLA
///     expires. Mirrors <c>BLOCKER.ESCALATED</c> (emitted before the blocker escalation
///     wait). Carries the reason.</description></item>
///   <item><description><c>CODE_REVIEW.ESCALATION_RESOLVED</c> — Story 4-6: the senior
///     resolved the escalation (approved / fixed) and the run proceeds to merge. The
///     resolution companion to <c>CODE_REVIEW.ESCALATED</c> (a rejection is recorded as
///     <c>CODE_REVIEW.FAILED</c>; an SLA expiry as the timed-out <c>CODE_REVIEW.FAILED</c>
///     terminal).</description></item>
///   <item><description><c>CODE_REVIEW.FAILED</c> — terminal: the review ended without a
///     merge (validation failure, senior rejection). LOUD (error-status) — never a silent
///     false success.</description></item>
/// </list>
/// </summary>
public static class CodeReviewEvents
{
    public const string PrCreatedSuccess = "CODE_REVIEW.PR_CREATED.SUCCESS";
    public const string PrCreatedFailed = "CODE_REVIEW.PR_CREATED.FAILED";
    public const string GuidanceDeliveredSuccess = "CODE_REVIEW.GUIDANCE_DELIVERED.SUCCESS";
    public const string GuidanceDeliveredFailed = "CODE_REVIEW.GUIDANCE_DELIVERED.FAILED";
    public const string IterationStarted = "CODE_REVIEW.ITERATION.STARTED";
    public const string MergedSuccess = "CODE_REVIEW.MERGED.SUCCESS";
    public const string MergedFailed = "CODE_REVIEW.MERGED.FAILED";

    /// <summary>Story 4-6 — an escalation was RAISED (senior notified, workflow suspended).</summary>
    public const string Escalated = "CODE_REVIEW.ESCALATED";

    /// <summary>Story 4-6 — an escalation was RESOLVED by the senior (proceeding to merge).</summary>
    public const string EscalationResolved = "CODE_REVIEW.ESCALATION_RESOLVED";

    public const string Failed = "CODE_REVIEW.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow inputs.
    /// Returns <c>null</c> for empty / single-user / unparseable values (code-review events
    /// in single-user mode are platform-scope, TenantId null). Mirrors
    /// <see cref="Tamma.Activities.Blocker.BlockerEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a creation/merge/guidance failure and the <see cref="Failed"/>
    /// terminal are LOUD (error-status) audit rows so a degraded/rejected outcome is never
    /// recorded as a false success; every other transition (PR created, guidance delivered,
    /// iteration started, merged, escalated) is a normal (success-status) row. An escalation
    /// is success-status because it is an expected, auditable hand-off to a senior — the
    /// rejection that may follow is recorded as <see cref="Failed"/>.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        PrCreatedFailed => "error",
        GuidanceDeliveredFailed => "error",
        MergedFailed => "error",
        Failed => "error",
        _ => "success",
    };
}
