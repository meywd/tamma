namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 39-9 (AC6) — DCB event-type constants for the deterministic repair ring,
/// emitted from <c>ManagedAgent</c> via the tenant <c>IEventRepository</c> (D4 — the
/// runner stays event-store-free and returns the history; <c>ManagedAgent</c> replays
/// it). Pattern <c>AGGREGATE.ACTION.STATUS</c> (per CLAUDE.md "Event Types"), matching
/// the neighbouring <see cref="AgentRunEventTypes"/> convention. Kept here as the single
/// source so composition code never hand-types a literal that could drift from the
/// Story 4-7 query / audit expectations.
///
/// <para>All three are tagged <c>{ issueId, documentType, role, action, repairTurn,
/// correlationId, tenantId }</c> so per-<c>(role, action) × documentType</c> cell
/// validation-failure / first-repair-success / exhaustion rates are computable from the
/// events alone (AC7).</para>
/// </summary>
public static class RepairRingEventTypes
{
    /// <summary>A produced document failed validation (emitted per failed validation,
    /// including the initial turn 0). Data carries the redacted violation summaries.</summary>
    public const string ValidationFailed = "LLM.VALIDATION.FAILED";

    /// <summary>A repair turn produced a valid document (data: the turn number).</summary>
    public const string RepairSucceeded = "LLM.REPAIR.SUCCEEDED";

    /// <summary>The repair cap was hit and the document is still invalid (data: turn
    /// count + final violation count).</summary>
    public const string RepairExhausted = "LLM.REPAIR.EXHAUSTED";
}
