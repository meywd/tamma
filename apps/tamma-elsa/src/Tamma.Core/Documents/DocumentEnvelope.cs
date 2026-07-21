using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Core;

namespace Tamma.Core.Documents;

/// <summary>
/// The immutable core document record (Story 39-2 AC1): identity, type, schema
/// version, lineage, producer provenance, lifecycle state, timestamps, and the
/// typed payload as JSON. Envelopes are values — a state transition produces a
/// NEW envelope via <see cref="WithState"/> (Design Decision D10), never a
/// mutation.
///
/// <para>
/// Every property carries an explicit <c>[JsonPropertyName]</c> (Design Decision
/// D8); serialize/deserialize through <see cref="DocumentJson.Options"/> so the
/// wire contract is deliberate. Timestamps are truncated to millisecond
/// precision at construction so JSON round-trips are exact.
/// </para>
/// </summary>
public sealed record DocumentEnvelope
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("issueId")]
    public required string IssueId { get; init; }

    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("parentDocumentId")]
    public Guid? ParentDocumentId { get; init; }

    [JsonPropertyName("supersedesDocumentId")]
    public Guid? SupersedesDocumentId { get; init; }

    [JsonPropertyName("producedBy")]
    public required DocumentProducer ProducedBy { get; init; }

    [JsonPropertyName("state")]
    public required DocumentState State { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    /// <summary>
    /// Mint a fresh <c>Draft</c> envelope with a UUID v7 identity. Timestamps are
    /// set to <paramref name="now"/> (defaulting to UTC now), truncated to
    /// millisecond precision.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.ENVELOPE.INVALID</c> for an empty <paramref name="issueId"/>
    /// or <paramref name="correlationId"/> — the lineage anchor is mandatory.
    /// </exception>
    public static DocumentEnvelope CreateDraft(
        DocumentTypeKey type,
        int schemaVersion,
        string issueId,
        string correlationId,
        DocumentProducer producedBy,
        JsonElement payload,
        Guid? parentDocumentId = null,
        Guid? supersedesDocumentId = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(issueId))
            throw Invalid("issueId", "The issueId lineage anchor must not be empty.");
        if (string.IsNullOrWhiteSpace(correlationId))
            throw Invalid("correlationId", "The correlationId must not be empty.");

        var timestamp = Truncate(now ?? DateTimeOffset.UtcNow);

        return new DocumentEnvelope
        {
            Id = UuidV7.NewGuid(timestamp),
            Type = type.ToWire(),
            SchemaVersion = schemaVersion,
            IssueId = issueId,
            CorrelationId = correlationId,
            ParentDocumentId = parentDocumentId,
            SupersedesDocumentId = supersedesDocumentId,
            ProducedBy = producedBy,
            State = DocumentState.Draft,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            Payload = payload.Clone(),
        };
    }

    /// <summary>
    /// Return a NEW envelope in state <paramref name="next"/> (Design Decision
    /// D10). The transition is validated by <see cref="DocumentStateMachine.AssertTransition"/>;
    /// <c>UpdatedAt</c> advances to <paramref name="now"/> (default UTC now),
    /// truncated to millisecond precision. The original instance is unchanged.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.STATE.ILLEGAL_TRANSITION</c> for an illegal transition.
    /// </exception>
    public DocumentEnvelope WithState(DocumentState next, DateTimeOffset? now = null)
    {
        DocumentStateMachine.AssertTransition(State, next);
        return this with
        {
            State = next,
            UpdatedAt = Truncate(now ?? DateTimeOffset.UtcNow),
        };
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private static TammaError Invalid(string field, string message) =>
        new(
            "DOCUMENT.ENVELOPE.INVALID",
            message,
            new Dictionary<string, object?> { ["field"] = field },
            retryable: false,
            severity: TammaErrorSeverity.High);

    // ---------------------------------------------------------------------
    // Value equality — the compiler-synthesized record equality would compare
    // Payload (a JsonElement struct) by its default (non-value) semantics, so a
    // round-tripped envelope would never equal its original. Override to compare
    // the payload by canonical raw text; everything else by value.
    // ---------------------------------------------------------------------

    public bool Equals(DocumentEnvelope? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id.Equals(other.Id)
            && Type == other.Type
            && SchemaVersion == other.SchemaVersion
            && IssueId == other.IssueId
            && CorrelationId == other.CorrelationId
            && Nullable.Equals(ParentDocumentId, other.ParentDocumentId)
            && Nullable.Equals(SupersedesDocumentId, other.SupersedesDocumentId)
            && ProducedBy == other.ProducedBy
            && State == other.State
            && CreatedAt.Equals(other.CreatedAt)
            && UpdatedAt.Equals(other.UpdatedAt)
            && Payload.GetRawText() == other.Payload.GetRawText();
    }

    public override int GetHashCode() =>
        HashCode.Combine(Id, Type, SchemaVersion, IssueId, CorrelationId, ProducedBy, State);
}
