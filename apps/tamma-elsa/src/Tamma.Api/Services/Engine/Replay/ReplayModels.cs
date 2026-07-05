namespace Tamma.Api.Services.Engine.Replay;

/// <summary>
/// Story 4-8 (black-box replay) — the point-in-time state view reconstructed by
/// folding a run's ordered DCB event slice. This is the RECONSTRUCTION half that
/// Story 4-7 (the event query API) deferred: 4-7 filters/pages the raw
/// <c>domain_events</c> stream; 4-8 folds one run's ordered slice into the
/// workflow/issue state as of a chosen point (a sequence number or timestamp).
///
/// <para>The fold is a PURE, deterministic left-fold over recorded events
/// (<see cref="ReplayReconstructor.Reconstruct"/>) — it re-executes nothing and
/// mutates nothing, so the same event slice always yields the same
/// <see cref="ReplayResult"/>.</para>
///
/// <para>AC3 shape — the reconstructed view surfaces: issue context
/// (<see cref="IssueNumber"/>/<see cref="Repository"/>), AI provider decisions
/// (<see cref="AiDecisions"/>), code changes (<see cref="CodeChanges"/>),
/// approval points (<see cref="Approvals"/>), and errors
/// (<see cref="Errors"/>) — plus the step reached (<see cref="StepReached"/>) and
/// a derived <see cref="Status"/>.</para>
/// </summary>
/// <param name="CorrelationId">The run / workflow-instance correlation id replayed.</param>
/// <param name="EventsReplayed">Number of events in the folded slice (up to the
/// chosen point).</param>
/// <param name="TotalEvents">Total events recorded for the run (all sequence
/// numbers), so a caller can tell whether the slice is the full run.</param>
/// <param name="ReplayedToEnd"><c>true</c> when the slice reached the last recorded
/// event (an <c>upTo</c> beyond the final event, or no <c>upTo</c> at all).</param>
/// <param name="AtSequenceNumber">The
/// <see cref="Tamma.Data.Entities.DomainEvent.SequenceNumber"/> of the last event in
/// the slice — the point-in-time marker. Null for an empty slice (a point before
/// the run began).</param>
/// <param name="AtTimestamp">The <c>CreatedAt</c> of the last event in the slice.</param>
/// <param name="StepReached">The event type of the last event in the slice — the
/// furthest-along step reconstructed. Null for an empty slice.</param>
/// <param name="Status">Derived terminal status: <c>running</c> until a terminal
/// event is folded, then <c>completed</c> / <c>failed</c> / <c>cancelled</c>.</param>
/// <param name="IssueNumber">The issue the run is scoped to (last non-null across
/// the slice), or null.</param>
/// <param name="Repository">The repository (from event tags/data), or null.</param>
/// <param name="Timeline">Every event in the slice as a lightweight ordered entry
/// (the black-box trail).</param>
/// <param name="AiDecisions">AI provider decisions (LLM/agent/research/design/
/// ambiguity events) in order.</param>
/// <param name="CodeChanges">Code-change and git artifacts (CODE/GIT/PR events) in
/// order.</param>
/// <param name="Approvals">Approval / quality-gate points in order.</param>
/// <param name="Errors">Failure events (cross-cutting: a failed AI call appears in
/// both <see cref="AiDecisions"/> and here) in order.</param>
/// <param name="Delta">Populated only when a <c>from</c> marker was supplied — the
/// pure diff between the fold-to-<c>from</c> and the fold-to-<c>upTo</c> (AC6).</param>
/// <param name="Truncated"><c>true</c> when the run has MORE events than the read cap
/// (<see cref="ReplayService"/>'s bounded <c>ListByCorrelationIdAsync</c>) — the fold
/// then reflects only the capped oldest-first slice. A pathological run signals
/// truncation rather than silently dropping the tail or materialising unbounded (the
/// "no silent truncation" rule + a DoS/memory guard).</param>
public sealed record ReplayResult(
    string CorrelationId,
    int EventsReplayed,
    int TotalEvents,
    bool ReplayedToEnd,
    long? AtSequenceNumber,
    DateTime? AtTimestamp,
    string? StepReached,
    string Status,
    int? IssueNumber,
    string? Repository,
    IReadOnlyList<ReplayTimelineEntry> Timeline,
    IReadOnlyList<ReplayDecision> AiDecisions,
    IReadOnlyList<ReplayArtifact> CodeChanges,
    IReadOnlyList<ReplayApproval> Approvals,
    IReadOnlyList<ReplayError> Errors,
    ReplayDelta? Delta,
    bool Truncated = false);

/// <summary>
/// Story 4-8 (review hardening) — thrown when a replay <c>from</c> marker resolves to a
/// point strictly AFTER the <c>upTo</c> replay point. The delta between two prefix folds
/// would then be a meaningless empty diff (the <c>from</c> fold is a superset of the
/// <c>upTo</c> fold), so the read fails loud; <see cref="Tamma.Api.Endpoints.EngineEndpoints.ReplayRun"/>
/// maps it to <c>400</c> rather than returning a misleading <c>200</c> with a zero delta.
/// </summary>
public sealed class ReplayRangeException(string message) : Exception(message);

/// <summary>One event in the reconstructed timeline (the ordered black-box trail).</summary>
/// <param name="Category">Domain bucket: <c>ai</c> | <c>code</c> | <c>approval</c> |
/// <c>issue</c> | <c>workflow</c> | <c>other</c>.</param>
public sealed record ReplayTimelineEntry(
    long SequenceNumber,
    string Type,
    DateTime CreatedAt,
    int? IssueNumber,
    string Category);

/// <summary>An AI provider decision (LLM call / agent task / research / design proposal).</summary>
public sealed record ReplayDecision(
    long SequenceNumber,
    string Type,
    DateTime CreatedAt,
    string? Provider,
    string? Model,
    string? Role,
    string? Outcome);

/// <summary>A code-change or git artifact produced during the run.</summary>
public sealed record ReplayArtifact(
    long SequenceNumber,
    string Type,
    DateTime CreatedAt,
    string? Detail);

/// <summary>An approval / quality-gate point.</summary>
public sealed record ReplayApproval(
    long SequenceNumber,
    string Type,
    DateTime CreatedAt,
    string? Decision);

/// <summary>A failure event encountered during the run.</summary>
public sealed record ReplayError(
    long SequenceNumber,
    string Type,
    DateTime CreatedAt,
    string? Message);

/// <summary>
/// AC6 — the diff between two folds of the SAME run: everything that happened
/// strictly after <see cref="FromSequenceNumber"/> up to the replay point. A pure
/// comparison of two prefix folds (see <see cref="ReplayReconstructor.Diff"/>).
/// </summary>
public sealed record ReplayDelta(
    long FromSequenceNumber,
    int AddedEventCount,
    int AddedDecisions,
    int AddedCodeChanges,
    int AddedApprovals,
    int AddedErrors,
    IReadOnlyList<ReplayTimelineEntry> AddedEvents);
