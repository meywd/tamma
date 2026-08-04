namespace Tamma.Activities.Adr;

/// <summary>
/// Story 41-9 (D6) — the <c>ADR.*</c> DCB event catalogue for the <c>adr-authoring</c> binding.
/// Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c> convention
/// (<c>CLAUDE.md</c>).
///
/// <para>The story names three members (<c>STARTED</c> / <c>DRAFTED</c> / <c>ACCEPTED</c>); D6
/// adds the LOUD terminal <see cref="Failed"/>, because every landed family carries one so a
/// degraded exit is never recorded as a success
/// (<c>DecompositionEvents.StatusForEvent</c> / <c>DocumentEvents.StatusForEvent</c>).</para>
///
/// <para>Per 41-2 D7 this family ships ONLY its constants: the emitter is the shared
/// <see cref="Tamma.Activities.Documents.EmitDomainLifecycleEventActivity"/>, which derives the
/// event status from the type suffix — <c>.FAILED</c> is an error row, <c>.STARTED</c> a started
/// row — so there is no per-family switch to forget. Every emission is tagged <c>issueId</c> /
/// <c>repository</c> / <c>tenantId</c> / <c>correlationId</c> and, once the lifecycle has minted
/// one, <c>documentId</c>.</para>
/// </summary>
public static class AdrEvents
{
    public const string Started = "ADR.STARTED";
    public const string Drafted = "ADR.DRAFTED";
    public const string Accepted = "ADR.ACCEPTED";

    // LOUD (error-status) terminal — a rejected / escalated exit must never read as a success.
    public const string Failed = "ADR.FAILED";
}
