namespace Tamma.Activities.Documents;

/// <summary>
/// Story 41-3 — the <c>BACKLOG.GROOMING.*</c> DCB event catalogue for the
/// <c>backlog-prioritization</c> binding. Type pattern follows the platform's
/// <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>).
///
/// <para>Per 41-3 D7 this family ships ONLY its constants: the emitter is 41-2's shared
/// <see cref="EmitDomainLifecycleEventActivity"/>, which derives the event status from the type
/// suffix — so <see cref="Failed"/> is a LOUD error row and <see cref="Started"/> a started row
/// with no per-family switch.</para>
///
/// <para><b>Tagging (D7).</b> A <c>BacklogOrdering</c> is NOT issue-scoped: it ranks a SET. The
/// emissions therefore ride the set-scoped lineage anchor
/// (<c>BacklogBindingHelper.BuildAnchor</c>, D2) as BOTH the <c>correlationId</c> and — because
/// <c>EmitDomainLifecycleEventActivity</c> keys its issue tag off one required string — the
/// <c>issueId</c> tag, exactly as the document row itself is anchored in the 39-11 store. The
/// <c>repository</c> and <c>tenantId</c> tags are the real ones; <c>backlogScope</c> rides the
/// event data payload.</para>
///
/// <list type="bullet">
///   <item><description><c>BACKLOG.GROOMING.STARTED</c> — a FRESH grooming run began (a
///     39-10 re-entry is not a new grooming run and emits nothing); data carries
///     <c>itemCount</c> and <c>backlogScope</c>.</description></item>
///   <item><description><c>BACKLOG.GROOMING.ORDERED</c> — the lifecycle produced and validated
///     an ordering draft; data carries <c>itemCount</c> and <c>evidenceHits</c>.</description></item>
///   <item><description><c>BACKLOG.GROOMING.ACCEPTED</c> — the accept gate accepted the
///     ordering; data carries the <c>documentId</c>.</description></item>
///   <item><description><c>BACKLOG.GROOMING.FAILED</c> — LOUD (error-status): the lifecycle
///     exited <c>rejected</c> / <c>escalated</c>; <c>Detail</c> names the typed outcome wire,
///     never a dead terminal.</description></item>
/// </list>
/// </summary>
public static class BacklogEvents
{
    public const string Started = "BACKLOG.GROOMING.STARTED";
    public const string Ordered = "BACKLOG.GROOMING.ORDERED";
    public const string Accepted = "BACKLOG.GROOMING.ACCEPTED";

    // LOUD (error-status) terminal — a non-accepted exit must never be recorded as a success.
    public const string Failed = "BACKLOG.GROOMING.FAILED";
}
