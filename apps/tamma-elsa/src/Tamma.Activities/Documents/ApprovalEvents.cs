namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-8 (Escalation &amp; Approval Surface) — central catalogue of the
/// <c>APPROVAL.*</c> / <c>ESCALATION.*</c> DCB event family: the uniform
/// approval-and-escalation surface that absorbs the old Story 4-6 goal, extended
/// with document lineage, transport <c>channel</c>, and time-to-resolve data.
/// Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention
/// (<c>CLAUDE.md</c>) and mirrors the sibling event catalogues (e.g.
/// <see cref="Tamma.Activities.Decomposition.DecompositionEvents"/>, and 39-6's
/// <c>DocumentEvents</c> — the "sibling <c>ApprovalEvents.cs</c>" the story names).
///
/// <para><b>Emit sites (each an atomic request+suspend or a single append):</b>
/// <list type="bullet">
///   <item><c>APPROVAL.REQUESTED</c> — the ACCEPT stage published an
///     <c>AcceptanceRequest</c> and the generic gate suspended (one path — to the
///     orchestrator). Emitted by <see cref="WaitForDocumentDecisionActivity"/>'s
///     <c>Execute</c>; <c>channel</c> is structurally <c>orchestrator</c>.</item>
///   <item><c>APPROVAL.PROVIDED</c> — the injected decision arrived on resume.
///     Emitted by the gate's callback; carries the server-derived decider +
///     <c>channel</c> + <c>durationMs</c>.</item>
///   <item><c>ESCALATION.TRIGGERED</c> — an unhandleable lifecycle outcome or an
///     always-escalate hit routed through the escalated exit region. Emitted by
///     <see cref="EmitEscalationEventActivity"/>; carries the full document
///     lineage (never a bare failure string).</item>
///   <item><c>ESCALATION.RESOLVED</c> — an escalation was dispositioned
///     (resolved/overridden/abandoned). Appended by the Tamma.Api
///     <c>EscalationDispositionService</c> (the lifecycle has already exited, so
///     disposition is an event append, not a workflow resume).</item>
/// </list></para>
///
/// <para><b>D9 — <c>DOCUMENT.ESCALATED</c> vs <c>ESCALATION.TRIGGERED</c>.</b>
/// Both are emitted at the same escalated exit and this is DELIBERATE, not
/// accidental double-emission: <c>DOCUMENT.ESCALATED</c> (39-6) is the
/// document-family STATE transition; <c>ESCALATION.TRIGGERED</c> (39-8) is the
/// EXCEPTION-SURFACE record that additionally carries lineage / <c>channel</c> /
/// timing. 39-11's dashboards treat <c>ESCALATION.*</c> as the exception surface
/// and <c>DOCUMENT.*</c> as state history so the two never double-count.</para>
///
/// <para><b>Pinned field names</b> (AC3 / the epic README promise — kept stable so
/// the 39-11 lineage API and dashboards can query them without a join):
/// tags <c>issueId</c> / <c>documentId</c> / <c>documentType</c> /
/// <c>correlationId</c> (+ <c>sessionId</c> on <c>APPROVAL.*</c>,
/// <c>escalationId</c> on <c>ESCALATION.*</c>, <c>tenantId</c> when set); data
/// <c>channel</c> (closed set <c>orchestrator | user | api</c>),
/// <c>requestedAtUtc</c>, and the denormalized <c>durationMs</c> on the resolving
/// (<c>PROVIDED</c> / <c>RESOLVED</c>) event.</para>
/// </summary>
public static class ApprovalEvents
{
    public const string Requested = "APPROVAL.REQUESTED";
    public const string Provided = "APPROVAL.PROVIDED";

    // The exception surface. TRIGGERED is a LOUD (error-status) row.
    public const string EscalationTriggered = "ESCALATION.TRIGGERED";
    public const string EscalationResolved = "ESCALATION.RESOLVED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (approval/escalation events in single-user mode are platform-scope,
    /// TenantId null). Mirrors <see cref="Tamma.Activities.Decomposition.DecompositionEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: an <c>ESCALATION.TRIGGERED</c> is a LOUD (error-status)
    /// audit row (the exception surface); the <c>APPROVAL.REQUESTED</c> request+suspend
    /// is an informational (started) row; every other transition
    /// (<c>APPROVAL.PROVIDED</c>, <c>ESCALATION.RESOLVED</c>) is a normal
    /// (success-status) row. A human REJECT is a legitimate decision, so
    /// <c>APPROVAL.PROVIDED</c> is a success row regardless of the decision kind.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        EscalationTriggered => "error",
        Requested => "started",
        _ => "success",
    };
}
