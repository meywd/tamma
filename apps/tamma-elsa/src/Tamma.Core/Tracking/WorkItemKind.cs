using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC1 — the closed hierarchy-kind vocabulary of a native work item.
/// Count-pinned at 4 by <c>WorkItemKindTests</c>; the CHECK constraint
/// <c>ck_work_items_kind</c> (created by 44-1) mirrors these exact wire strings.
///
/// <para><b><c>bug</c> and <c>chore</c> are deliberately absent.</b>
/// <see cref="TriageIssueType"/> (<c>TriageDecision.cs</c>) already carries both,
/// and it is bound as the work item's <em>type</em> axis (see
/// <see cref="TrackerPriority"/>). A <c>Kind=Bug</c> would be a second,
/// partially-overlapping vocabulary for the same fact — <c>(Kind=Bug,
/// Type=Feature)</c> and <c>(Kind=Story, Type=Bug)</c> would both be
/// representable and neither would mean anything. Kind answers <em>what may
/// contain what</em>; type answers <em>what sort of thing is it</em>. Two axes,
/// two vocabularies, no overlap.</para>
///
/// <para>Distinct from <see cref="Documents.DocumentInstanceStatus"/>'s world: a
/// document describes a revision under review; a work item is a thing to be
/// done. See <see cref="WorkItemStatus"/> for the status-side twin of this
/// note.</para>
/// </summary>
public enum WorkItemKind
{
    [Wire("epic")] Epic,
    [Wire("story")] Story,
    [Wire("task")] Task,
    [Wire("spike")] Spike,
}

public static class WorkItemKindExtensions
{
    /// <summary>The canonical wire string for <paramref name="kind"/>.</summary>
    public static string ToWire(this WorkItemKind kind) => EnumWire<WorkItemKind>.ToWire(kind);

    /// <summary>Case-sensitive (ordinal) lookup of the member for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out WorkItemKind kind) =>
        EnumWire<WorkItemKind>.TryParse(wire, out kind);

    /// <summary>
    /// Resolve a wire string to a <see cref="WorkItemKind"/> (case-sensitive, ordinal).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>TRACKER.UNKNOWN_KIND</c> for null, empty, or unknown input.
    /// </exception>
    public static WorkItemKind Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<WorkItemKind>.TryParse(input, out var kind))
            return kind;

        throw new TammaError(
            "TRACKER.UNKNOWN_KIND",
            $"Unknown work item kind: '{input}'. Valid kinds: " +
            $"{string.Join(", ", Enum.GetValues<WorkItemKind>().Select(k => k.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}
