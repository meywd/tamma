using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// Story 39-11 — the closed status vocabulary of a persisted
/// <c>document_instances</c> row (Design Decision D3). This is the STORE's status
/// dimension, distinct from 39-2's lifecycle <see cref="DocumentState"/>: it adds
/// two store-only members the state machine has no equivalent for —
/// <see cref="InReview"/> (a document waiting on a review round) and
/// <see cref="Superseded"/> (a prior revision retired by the single write door,
/// D4). The set is count-pinned at 7 by <c>DocumentInstanceStatusTests</c>; the
/// CHECK constraint <c>ck_document_instances_status</c> mirrors these exact wire
/// strings.
///
/// <para>Note the underscore in <c>in_review</c> — the only multi-word wire
/// string; every other member's wire is a single lowercase token.</para>
/// </summary>
public enum DocumentInstanceStatus
{
    [Wire("draft")]      Draft,
    [Wire("validated")]  Validated,
    [Wire("in_review")]  InReview,
    [Wire("accepted")]   Accepted,
    [Wire("rejected")]   Rejected,
    [Wire("superseded")] Superseded,
    [Wire("escalated")]  Escalated,
}

public static class DocumentInstanceStatusExtensions
{
    /// <summary>The canonical wire string for <paramref name="status"/>.</summary>
    public static string ToWire(this DocumentInstanceStatus status) =>
        EnumWire<DocumentInstanceStatus>.ToWire(status);

    /// <summary>
    /// Resolve a wire string to a <see cref="DocumentInstanceStatus"/>
    /// (case-sensitive, ordinal).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.STORE.UNKNOWN_STATUS</c> for null, empty, or unknown input.
    /// </exception>
    public static DocumentInstanceStatus Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) &&
            EnumWire<DocumentInstanceStatus>.TryParse(input, out var status))
            return status;

        throw new TammaError(
            "DOCUMENT.STORE.UNKNOWN_STATUS",
            $"Unknown document instance status: '{input}'. Valid statuses: " +
            $"{string.Join(", ", Enum.GetValues<DocumentInstanceStatus>().Select(s => s.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// The total map from 39-2's lifecycle <see cref="DocumentState"/> onto the
    /// store status set (Design Decision D3): <c>Draft→draft</c>,
    /// <c>Validated→validated</c>, <c>Reviewed→in_review</c>,
    /// <c>Accepted→accepted</c>, <c>Rejected→rejected</c>,
    /// <c>Escalated→escalated</c>. It NEVER yields <see cref="Superseded"/> — that
    /// is a store-only status, set exclusively by the supersession write (D4), so
    /// no state transition can produce it.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.STORE.UNKNOWN_STATUS</c> for an unmapped
    /// <see cref="DocumentState"/> (unreachable while the enum has its 6 members —
    /// a fail-loud guard against a future member landing unmapped).
    /// </exception>
    public static DocumentInstanceStatus FromState(DocumentState state) => state switch
    {
        DocumentState.Draft => DocumentInstanceStatus.Draft,
        DocumentState.Validated => DocumentInstanceStatus.Validated,
        DocumentState.Reviewed => DocumentInstanceStatus.InReview,
        DocumentState.Accepted => DocumentInstanceStatus.Accepted,
        DocumentState.Rejected => DocumentInstanceStatus.Rejected,
        DocumentState.Escalated => DocumentInstanceStatus.Escalated,
        _ => throw new TammaError(
            "DOCUMENT.STORE.UNKNOWN_STATUS",
            $"No store status mapping for document state '{state}'. FromState must be " +
            "total over every DocumentState member.",
            new Dictionary<string, object?> { ["state"] = state.ToString() },
            retryable: false,
            severity: TammaErrorSeverity.Critical),
    };
}
