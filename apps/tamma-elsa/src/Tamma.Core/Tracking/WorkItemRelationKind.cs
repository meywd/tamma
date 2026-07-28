using Tamma.Api.Services.Agents;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC14 — the closed relation vocabulary between work items.
/// Count-pinned at 3 by <c>WorkItemRelationKindTests</c>.
///
/// <para><b>Why it exists:</b> <c>blocked</c> is a <em>status</em>
/// (<see cref="WorkItemStatus.Blocked"/>) with no way to record <em>what</em>
/// blocks the item — a half-feature. Worse, without a relation edge, "A must
/// land before B" has exactly one place to go: parenting — and
/// dependency-as-hierarchy corrupts the tree that 44-3's recursive CTE, 44-4's
/// board roll-ups and 44-9's <c>sprint-status.yaml</c> generation all read.
/// Dependency is not new here: <c>DecompositionTask.DependsOn</c> and
/// <c>PlanTask.DependsOn</c> already ship it inside document bodies.</para>
///
/// <para><b>Direction convention (this story's contract):</b>
/// <see cref="Blocks"/> is directed source→target ("source blocks target");
/// <see cref="Duplicate"/> and <see cref="Related"/> are symmetric and stored
/// canonically with the lower id first (see
/// <see cref="WorkItemRelationKindExtensions.Canonicalize"/>), so an edge cannot
/// be inserted twice in mirror form.</para>
///
/// <para><b>Consumers:</b> the <c>work_item_relations</c> edge table (source,
/// target, kind, unique index) is 44-1's; its enforcement — no self-edge, no
/// cross-project edge, and deliberately <b>no cycle detection</b> (a blocking
/// cycle is a real situation a user should be shown rather than prevented from
/// recording) — is 44-3's. If the product owner declines the relation feature,
/// <b>delete this enum</b> rather than leaving it unreferenced — the repo
/// already carries <c>Issue</c> and <c>TriageComplexity</c> as dead
/// vocabularies and the epic README spends a table on why that is expensive.</para>
/// </summary>
public enum WorkItemRelationKind
{
    [Wire("blocks")] Blocks,
    [Wire("duplicate")] Duplicate,
    [Wire("related")] Related,
}

public static class WorkItemRelationKindExtensions
{
    /// <summary>The canonical wire string for <paramref name="kind"/>.</summary>
    public static string ToWire(this WorkItemRelationKind kind) =>
        EnumWire<WorkItemRelationKind>.ToWire(kind);

    /// <summary>Case-sensitive (ordinal) lookup of the member for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out WorkItemRelationKind kind) =>
        EnumWire<WorkItemRelationKind>.TryParse(wire, out kind);

    /// <summary>
    /// Resolve a wire string to a <see cref="WorkItemRelationKind"/>
    /// (case-sensitive, ordinal).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>TRACKER.UNKNOWN_RELATION_KIND</c> for null, empty, or unknown input.
    /// </exception>
    public static WorkItemRelationKind Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<WorkItemRelationKind>.TryParse(input, out var kind))
            return kind;

        throw new TammaError(
            "TRACKER.UNKNOWN_RELATION_KIND",
            $"Unknown work item relation kind: '{input}'. Valid kinds: " +
            $"{string.Join(", ", Enum.GetValues<WorkItemRelationKind>().Select(k => k.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Whether <paramref name="kind"/> is symmetric (undirected).
    /// <see cref="WorkItemRelationKind.Blocks"/> is the only directed kind.
    /// </summary>
    public static bool IsSymmetric(this WorkItemRelationKind kind) =>
        kind is WorkItemRelationKind.Duplicate or WorkItemRelationKind.Related;

    /// <summary>
    /// The single implementation of the direction convention: rejects a
    /// self-relation, and for symmetric kinds returns the endpoints ordered
    /// lower id first (by <see cref="Guid.CompareTo(Guid)"/>) so a symmetric
    /// edge has exactly one storable form and cannot be inserted twice in
    /// mirror form. Directed <see cref="WorkItemRelationKind.Blocks"/> edges are
    /// returned unchanged — source→target is meaning, not storage order.
    /// 44-1's unique index and 44-3's validation both call this.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>TRACKER.SELF_RELATION</c> when
    /// <paramref name="sourceId"/> == <paramref name="targetId"/>.
    /// </exception>
    public static (Guid SourceId, Guid TargetId) Canonicalize(
        this WorkItemRelationKind kind, Guid sourceId, Guid targetId)
    {
        if (sourceId == targetId)
        {
            throw new TammaError(
                "TRACKER.SELF_RELATION",
                $"A work item cannot relate to itself ({kind.ToWire()}).",
                new Dictionary<string, object?> { ["workItemId"] = sourceId, ["kind"] = kind.ToWire() },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        if (kind.IsSymmetric() && targetId.CompareTo(sourceId) < 0)
            return (targetId, sourceId);

        return (sourceId, targetId);
    }
}
