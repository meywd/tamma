using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Engine.Replay;

/// <summary>
/// Story 4-8 — the PURE, deterministic core of black-box replay. Given a run's
/// ordered DCB event slice it folds the events (left-to-right) into a
/// <see cref="ReplayResult"/> point-in-time state view. No I/O, no clock, no
/// randomness, no side effects: the same input slice always yields an equal
/// result (determinism), and nothing is re-executed or mutated (read-only by
/// construction — this class never touches the Elsa runtime, a repository, or a
/// DbContext).
///
/// <para>The event source is Story 4-7's
/// <see cref="Tamma.Data.Repositories.IEventRepository.ListByCorrelationIdAsync"/>
/// (all of a run's events, ordered by
/// <see cref="DomainEvent.SequenceNumber"/>); this class only transforms that
/// already-fetched, already-ordered list.</para>
/// </summary>
public static class ReplayReconstructor
{
    /// <summary>
    /// Slice an ordered (ascending <see cref="DomainEvent.SequenceNumber"/>) run to
    /// the events at or before the chosen point. <paramref name="upToSequence"/>
    /// keeps events with <c>SequenceNumber &lt;= upToSequence</c>;
    /// <paramref name="upToTimestamp"/> keeps events with
    /// <c>CreatedAt &lt;= upToTimestamp</c>. When neither is supplied the full run
    /// is returned. A point before the run began yields an empty slice (the run is
    /// still known — that is a valid as-of view, not a 404).
    /// </summary>
    public static IReadOnlyList<DomainEvent> SliceUpTo(
        IReadOnlyList<DomainEvent> ordered,
        long? upToSequence,
        DateTimeOffset? upToTimestamp)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        IEnumerable<DomainEvent> slice = ordered;
        if (upToSequence is { } seq)
        {
            slice = slice.Where(e => e.SequenceNumber <= seq);
        }
        if (upToTimestamp is { } ts)
        {
            var tsUtc = ts.UtcDateTime;
            slice = slice.Where(e => e.CreatedAt <= tsUtc);
        }
        return slice.ToList();
    }

    /// <summary>
    /// Fold an ordered event <paramref name="slice"/> into the reconstructed
    /// point-in-time state. <paramref name="totalEvents"/> is the full run's event
    /// count (so the result reports whether the slice reached the end).
    /// </summary>
    public static ReplayResult Reconstruct(
        string correlationId,
        IReadOnlyList<DomainEvent> slice,
        int totalEvents,
        ReplayDelta? delta = null)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(slice);

        var timeline = new List<ReplayTimelineEntry>(slice.Count);
        var decisions = new List<ReplayDecision>();
        var artifacts = new List<ReplayArtifact>();
        var approvals = new List<ReplayApproval>();
        var errors = new List<ReplayError>();

        int? issueNumber = null;
        string? repository = null;
        var status = "running";

        // Left-fold: apply each recorded event to the accumulating state, in order.
        foreach (var e in slice)
        {
            var tags = SafeJsonToStringMap(e.Tags);
            var data = SafeParse(e.Data);
            var category = Categorize(e.Type);

            timeline.Add(new ReplayTimelineEntry(
                e.SequenceNumber, e.Type, e.CreatedAt, e.IssueNumber, category));

            if (e.IssueNumber is int n)
            {
                issueNumber = n;
            }
            var repo = ReadString(tags, "repository") ?? ReadJsonString(data, "repository");
            if (!string.IsNullOrEmpty(repo))
            {
                repository = repo;
            }

            if (IsAiDecision(e.Type))
            {
                decisions.Add(new ReplayDecision(
                    e.SequenceNumber, e.Type, e.CreatedAt,
                    Provider: ReadString(tags, "provider") ?? ReadJsonString(data, "provider"),
                    Model: ReadString(tags, "model") ?? ReadJsonString(data, "model"),
                    Role: ReadString(tags, "role") ?? ReadJsonString(data, "role"),
                    Outcome: DecisionOutcome(e.Type)));
            }

            if (IsCodeChange(e.Type))
            {
                artifacts.Add(new ReplayArtifact(
                    e.SequenceNumber, e.Type, e.CreatedAt,
                    Detail: ArtifactDetail(data)));
            }

            if (IsApproval(e.Type))
            {
                approvals.Add(new ReplayApproval(
                    e.SequenceNumber, e.Type, e.CreatedAt,
                    Decision: ApprovalDecision(e.Type, tags, data)));
            }

            if (IsError(e.Type))
            {
                errors.Add(new ReplayError(
                    e.SequenceNumber, e.Type, e.CreatedAt,
                    Message: ErrorMessage(e, tags, data)));
            }

            // Terminal-status transitions — the LAST terminal event folded wins.
            var terminal = TerminalStatus(e.Type);
            if (terminal is not null)
            {
                status = terminal;
            }
        }

        var last = slice.Count > 0 ? slice[^1] : null;

        return new ReplayResult(
            CorrelationId: correlationId,
            EventsReplayed: slice.Count,
            TotalEvents: totalEvents,
            ReplayedToEnd: slice.Count >= totalEvents,
            AtSequenceNumber: last?.SequenceNumber,
            AtTimestamp: last?.CreatedAt,
            StepReached: last?.Type,
            Status: status,
            IssueNumber: issueNumber,
            Repository: repository,
            Timeline: timeline,
            AiDecisions: decisions,
            CodeChanges: artifacts,
            Approvals: approvals,
            Errors: errors,
            Delta: delta);
    }

    /// <summary>
    /// AC6 — the pure diff between two prefix folds of the SAME run. Both
    /// <paramref name="older"/> and <paramref name="newer"/> come from
    /// <see cref="Reconstruct"/> over prefix slices, so <paramref name="newer"/>'s
    /// timeline is a superset of <paramref name="older"/>'s; the delta is the set of
    /// events with a sequence number not present in the older fold — i.e. everything
    /// that happened between the two points. No new events are computed — this is a
    /// comparison of two already-folded states.
    /// </summary>
    public static ReplayDelta Diff(ReplayResult older, ReplayResult newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        var olderSeqs = older.Timeline.Select(t => t.SequenceNumber).ToHashSet();
        var added = newer.Timeline.Where(t => !olderSeqs.Contains(t.SequenceNumber)).ToList();
        var addedSeqs = added.Select(a => a.SequenceNumber).ToHashSet();

        var fromSeq = older.AtSequenceNumber ?? 0L;

        return new ReplayDelta(
            FromSequenceNumber: fromSeq,
            AddedEventCount: added.Count,
            AddedDecisions: newer.AiDecisions.Count(d => addedSeqs.Contains(d.SequenceNumber)),
            AddedCodeChanges: newer.CodeChanges.Count(c => addedSeqs.Contains(c.SequenceNumber)),
            AddedApprovals: newer.Approvals.Count(a => addedSeqs.Contains(a.SequenceNumber)),
            AddedErrors: newer.Errors.Count(x => addedSeqs.Contains(x.SequenceNumber)),
            AddedEvents: added);
    }

    // ─── categorization (pure, type-driven) ───────────────────────────────────

    /// <summary>Single domain bucket for the timeline. Approval &gt; code &gt; ai
    /// &gt; issue &gt; workflow &gt; other. A failure is a cross-cut (see
    /// <see cref="IsError"/>), not a timeline category, so a failed AI call still
    /// reads as an <c>ai</c> step on the timeline while also appearing in
    /// <see cref="ReplayResult.Errors"/>.</summary>
    internal static string Categorize(string type)
    {
        if (IsApproval(type)) return "approval";
        if (IsCodeChange(type)) return "code";
        if (IsAiDecision(type)) return "ai";
        if (type.StartsWith("ISSUE.", StringComparison.Ordinal)) return "issue";
        if (IsWorkflowStep(type)) return "workflow";
        return "other";
    }

    internal static bool IsAiDecision(string type) =>
        type.StartsWith("LLM.CALL", StringComparison.Ordinal)
        || type.StartsWith("AGENT.TASK", StringComparison.Ordinal)
        || type.StartsWith("AGENT.RUN", StringComparison.Ordinal)
        || type.StartsWith("AGENT.TOOL_CALL", StringComparison.Ordinal)
        || type.StartsWith("AGENT.RESULTS", StringComparison.Ordinal)
        || type.StartsWith("AGENT.ITERATION", StringComparison.Ordinal)
        || type.StartsWith("RESEARCH.", StringComparison.Ordinal)
        || type.StartsWith("DESIGN.PROPOSAL", StringComparison.Ordinal)
        || type.StartsWith("AMBIGUITY.", StringComparison.Ordinal);

    internal static bool IsCodeChange(string type) =>
        type.StartsWith("CODE.", StringComparison.Ordinal)
        || type.StartsWith("GIT.", StringComparison.Ordinal)
        || type.StartsWith("PR.", StringComparison.Ordinal);

    internal static bool IsApproval(string type) =>
        type.StartsWith("APPROVAL", StringComparison.Ordinal)
        || type.StartsWith("GATE.", StringComparison.Ordinal)
        || type.Contains(".APPROVAL", StringComparison.Ordinal)
        || type.EndsWith(".APPROVED", StringComparison.Ordinal)
        || type.EndsWith(".REJECTED", StringComparison.Ordinal)
        || type.Contains("APPROVAL_REQUESTED", StringComparison.Ordinal);

    internal static bool IsError(string type) =>
        type.EndsWith(".FAILED", StringComparison.Ordinal)
        || type.EndsWith(".ERROR", StringComparison.Ordinal)
        || type.Contains(".STEP_FAILED", StringComparison.Ordinal)
        || type.Contains(".RETRY_EXCEEDED", StringComparison.Ordinal)
        || type.Contains(".TIMED_OUT", StringComparison.Ordinal);

    internal static bool IsWorkflowStep(string type) =>
        type.StartsWith("WORKFLOW.", StringComparison.Ordinal)
        || type.StartsWith("CYCLE.", StringComparison.Ordinal)
        || type.StartsWith("ADL.", StringComparison.Ordinal);

    /// <summary>Map an event type to a terminal run status, or null if not terminal.</summary>
    internal static string? TerminalStatus(string type) => type switch
    {
        "WORKFLOW.COMPLETED" or "CYCLE.COMPLETED" => "completed",
        "WORKFLOW.FAILED" or "CYCLE.FAILED" or "WORKFLOW.RETRY_EXCEEDED" => "failed",
        "WORKFLOW.CANCELLED" => "cancelled",
        _ => null,
    };

    private static string? DecisionOutcome(string type)
    {
        if (type.EndsWith(".SUCCESS", StringComparison.Ordinal)) return "success";
        if (type.EndsWith(".FAILED", StringComparison.Ordinal)) return "failed";
        if (type.EndsWith(".PARTIAL", StringComparison.Ordinal)) return "partial";
        return null;
    }

    private static string? ApprovalDecision(
        string type, IReadOnlyDictionary<string, string?> tags, JsonElement? data)
    {
        if (type.EndsWith(".APPROVED", StringComparison.Ordinal)) return "approved";
        if (type.EndsWith(".REJECTED", StringComparison.Ordinal)) return "rejected";
        if (type.Contains("APPROVAL_REQUESTED", StringComparison.Ordinal)
            || type.Contains(".WAIT", StringComparison.Ordinal)) return "pending";
        return ReadString(tags, "decision")
            ?? ReadString(tags, "status")
            ?? ReadJsonString(data, "decision");
    }

    private static string? ArtifactDetail(JsonElement? data)
    {
        // Best-effort human-readable artifact detail — never throws, null when absent.
        return ReadJsonString(data, "pullRequestUrl")
            ?? ReadJsonString(data, "prUrl")
            ?? ReadJsonString(data, "htmlUrl")
            ?? ReadJsonString(data, "branch")
            ?? ReadJsonString(data, "branchName")
            ?? ReadJsonString(data, "sha")
            ?? ReadJsonString(data, "commitSha");
    }

    private static string? ErrorMessage(
        DomainEvent e, IReadOnlyDictionary<string, string?> tags, JsonElement? data)
    {
        // Metadata.error is where the engine append path records the failure reason.
        var meta = SafeParse(e.Metadata);
        return ReadJsonString(meta, "error")
            ?? ReadJsonString(data, "error")
            ?? ReadString(tags, "error")
            ?? ReadJsonString(data, "exitReason");
    }

    // ─── JSON helpers (safe, never throw) ─────────────────────────────────────

    private static JsonElement? SafeParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string?> SafeJsonToStringMap(string? raw)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        var el = SafeParse(raw);
        if (el is not { ValueKind: JsonValueKind.Object } obj) return map;
        foreach (var prop in obj.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : prop.Value.GetRawText();
        }
        return map;
    }

    private static string? ReadString(IReadOnlyDictionary<string, string?> map, string key) =>
        map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static string? ReadJsonString(JsonElement? element, string key)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!obj.TryGetProperty(key, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => null,
        };
    }
}
