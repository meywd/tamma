namespace Tamma.Activities.Documents;

/// <summary>
/// Story 41-2 — the <c>ACCEPTANCE_CRITERIA.*</c> DCB event catalogue for the
/// <c>acceptance-criteria-authoring</c> binding. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>).
///
/// <para>Per 41-2 D7 this family ships ONLY its constants: the emitter is the shared
/// <see cref="EmitDomainLifecycleEventActivity"/>, which derives the event status from the type
/// suffix — so <see cref="Failed"/> is a LOUD error row and <see cref="Started"/> a started row
/// with no per-family switch. Every emission is tagged <c>issueId</c> / <c>repository</c> /
/// <c>tenantId</c> / <c>correlationId</c> and, once the lifecycle has minted one,
/// <c>documentId</c>.</para>
///
/// <list type="bullet">
///   <item><description><c>ACCEPTANCE_CRITERIA.STARTED</c> — a FRESH authoring run began (a
///     39-10 re-entry is not a new authoring run and emits nothing).</description></item>
///   <item><description><c>ACCEPTANCE_CRITERIA.DRAFTED</c> — the lifecycle produced and
///     validated a draft; data carries <c>consumedDocumentIds</c> and
///     <c>criteriaCount</c>.</description></item>
///   <item><description><c>ACCEPTANCE_CRITERIA.ACCEPTED</c> — the accept gate accepted the
///     document; data carries the <c>documentId</c>.</description></item>
///   <item><description><c>ACCEPTANCE_CRITERIA.FAILED</c> — LOUD (error-status): the lifecycle
///     exited <c>rejected</c> / <c>escalated</c>; <c>Detail</c> names the typed outcome
///     wire, never a dead terminal.</description></item>
/// </list>
/// </summary>
public static class AcceptanceCriteriaEvents
{
    public const string Started = "ACCEPTANCE_CRITERIA.STARTED";
    public const string Drafted = "ACCEPTANCE_CRITERIA.DRAFTED";
    public const string Accepted = "ACCEPTANCE_CRITERIA.ACCEPTED";

    // LOUD (error-status) terminal — a non-accepted exit must never be recorded as a success.
    public const string Failed = "ACCEPTANCE_CRITERIA.FAILED";
}
