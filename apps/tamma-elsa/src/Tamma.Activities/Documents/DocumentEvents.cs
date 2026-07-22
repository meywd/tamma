namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-6 (DocumentLifecycleWorkflow) — central catalogue of the
/// <c>DOCUMENT.*</c> DCB event types emitted on every transition of the generic
/// document lifecycle (<c>produce → validate → review → revise → accept</c>) via
/// <see cref="EmitDocumentEventActivity"/>. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and mirrors the
/// sibling catalogues (<see cref="Tamma.Activities.Decomposition.DecompositionEvents"/>,
/// <see cref="ApprovalEvents"/>).
///
/// <para>Replaying the <c>DOCUMENT.*</c> stream for an issue, ordered by timestamp,
/// reconstructs the lifecycle's transition history (Story 39-6 AC5 — asserted in
/// the execution test's replay scenario). Events are emitted via
/// <c>TammaEventEmitter.Emit</c> into the workflow's <c>tamma:events</c> transient
/// list and persisted <i>durably</i> by the engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern the
/// sibling catalogues use. No activity holds a DB / repository dependency of its
/// own.</para>
///
/// <para><b>D9 boundary.</b> This family covers the document STATE history only.
/// <c>APPROVAL.*</c> / <c>ESCALATION.*</c> (39-8, <see cref="ApprovalEvents"/>) are
/// the acceptance-gate + exception surface and are emitted by 39-8's own
/// gate/escalation activities — this story emits ONLY <c>DOCUMENT.*</c>.</para>
/// </summary>
public static class DocumentEvents
{
    public const string ProducedSuccess = "DOCUMENT.PRODUCED.SUCCESS";
    public const string ProducedFailed = "DOCUMENT.PRODUCED.FAILED";
    public const string ValidatedSuccess = "DOCUMENT.VALIDATED.SUCCESS";
    public const string ValidatedFailed = "DOCUMENT.VALIDATED.FAILED";
    public const string ReviewRequested = "DOCUMENT.REVIEW_REQUESTED";
    public const string Reviewed = "DOCUMENT.REVIEWED";
    public const string RevisionStarted = "DOCUMENT.REVISION_STARTED";
    public const string Accepted = "DOCUMENT.ACCEPTED";

    // LOUD (error-status) terminals — a rejected/escalated document must never be
    // recorded as a false success.
    public const string Rejected = "DOCUMENT.REJECTED";
    public const string Escalated = "DOCUMENT.ESCALATED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (document events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="Tamma.Activities.Decomposition.DecompositionEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: the <c>.FAILED</c> validation/produce rows and the
    /// <c>DOCUMENT.REJECTED</c> / <c>DOCUMENT.ESCALATED</c> terminals are LOUD
    /// (error-status) audit rows; <c>DOCUMENT.REVIEW_REQUESTED</c> and
    /// <c>DOCUMENT.REVISION_STARTED</c> are informational (started) rows; every
    /// other transition (produce/validate success, reviewed, accepted) is a normal
    /// (success-status) row. Keeps a degraded terminal from ever being recorded as
    /// a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        ProducedFailed => "error",
        ValidatedFailed => "error",
        Rejected => "error",
        Escalated => "error",
        ReviewRequested => "started",
        RevisionStarted => "started",
        _ => "success",
    };
}
