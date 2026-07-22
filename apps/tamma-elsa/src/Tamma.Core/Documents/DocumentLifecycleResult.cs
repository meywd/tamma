using System.Text.Json.Serialization;

namespace Tamma.Core.Documents;

/// <summary>
/// A minimal reference to one draft envelope in a lifecycle's lineage (Story 39-6
/// Design Decision D7): the envelope id and its terminal state wire string. The
/// full envelope lives in the workflow's <c>LifecycleState</c>; this is the
/// audit-facing projection carried on the <see cref="DocumentLifecycleResult"/>.
/// </summary>
public sealed record DraftRef(
    [property: JsonPropertyName("id")]    Guid Id,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// The full lineage of one lifecycle run (Story 39-6 Design Decision D7): every
/// draft envelope (id + state) in the supersedes chain, every review envelope id,
/// the rounds and repair attempts used, the last deterministic-validation
/// violations (domain-phrased, non-empty on a validation-exhaustion escalation),
/// and a reference to the effective acceptance rules the run was gated under.
///
/// <para>Every wire property carries an explicit <c>[JsonPropertyName]</c> (39-2
/// D8); serialize/deserialize through <see cref="DocumentJson.Options"/>.</para>
/// </summary>
public sealed record DocumentLineage(
    [property: JsonPropertyName("drafts")]              IReadOnlyList<DraftRef> Drafts,
    [property: JsonPropertyName("reviewIds")]           IReadOnlyList<Guid> ReviewIds,
    [property: JsonPropertyName("roundsUsed")]          int RoundsUsed,
    [property: JsonPropertyName("repairAttemptsUsed")]  int RepairAttemptsUsed,
    [property: JsonPropertyName("lastViolations")]      IReadOnlyList<DocumentViolation> LastViolations,
    [property: JsonPropertyName("rulesReference")]      string? RulesReference);

/// <summary>
/// The exit contract of <c>DocumentLifecycleWorkflow</c> (Story 39-6 AC3; Design
/// Decision D7). Three terminal statuses — <c>accepted</c>, <c>rejected</c>,
/// <c>escalated</c> — each carrying the full <see cref="DocumentLineage"/>. The
/// <see cref="Outcome"/> is <b>null on BOTH <c>accepted</c> and <c>rejected</c>
/// and non-null ONLY on <c>escalated</c></b>, so a parent workflow CANNOT
/// distinguish accepted from rejected by the outcome enum — parents MUST switch on
/// <see cref="Status"/> FIRST and read <see cref="Outcome"/> only on the
/// <c>escalated</c> branch.
///
/// <para>Owned by this story per AC3 ("the outcome enum is owned by 39-2; the
/// lineage-carrying result is 39-6's"). Every wire property carries an explicit
/// <c>[JsonPropertyName]</c>; serialize through <see cref="DocumentJson.Options"/>.</para>
/// </summary>
public sealed record DocumentLifecycleResult(
    [property: JsonPropertyName("status")]     string Status,
    [property: JsonPropertyName("outcome")]    DocumentLifecycleOutcome? Outcome,
    [property: JsonPropertyName("documentId")] Guid? DocumentId,
    [property: JsonPropertyName("lineage")]    DocumentLineage Lineage)
{
    /// <summary>The <c>accepted</c> status wire string.</summary>
    public const string StatusAccepted = "accepted";

    /// <summary>The <c>rejected</c> status wire string.</summary>
    public const string StatusRejected = "rejected";

    /// <summary>The <c>escalated</c> status wire string.</summary>
    public const string StatusEscalated = "escalated";
}
