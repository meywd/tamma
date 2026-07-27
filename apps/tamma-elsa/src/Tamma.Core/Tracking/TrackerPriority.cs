using System.Collections.Frozen;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC11/AC12 — the tracker's binding to the shipped triage
/// vocabularies. <b>Not a new enum</b>: the work item's priority binds to
/// <see cref="TriagePriority"/><c>?</c> and its type to
/// <see cref="TriageIssueType"/> (<c>TriageDecision.cs</c>), giving two dead
/// vocabularies their first consumer instead of adding a fifth priority
/// vocabulary to a repo whose triage enums already drift from the
/// <c>triage-intake</c> prompt. <c>bug</c>/<c>chore</c> live on this type axis
/// only — they are deliberately not <see cref="WorkItemKind"/> members.
///
/// <para><b>Priority is nullable.</b> <c>null</c> ("nobody has prioritised
/// this") and <c>normal</c> ("somebody looked and said normal") are different
/// facts, and in an overnight agent-filed queue the difference is the signal.
/// <see cref="SortKey"/> pins the sort rule once: unset sorts <b>after</b>
/// <c>low</c>.</para>
///
/// <para><c>[Wire]</c> pins strings, not ordinals — <c>TriagePriority</c>'s
/// declaration order <c>(Urgent, High, Normal, Low) == (0, 1, 2, 3)</c> is what
/// every priority-sorted board and <c>ORDER BY</c> in 44-3/44-4/44-7 rests on,
/// so it gets its own pin in <c>TrackerPriorityTests</c>.</para>
///
/// <para><see cref="TriageComplexity"/> is explicitly NOT adopted: its
/// <c>[Wire("epic")]</c> member is a size estimate and would read as a hierarchy
/// level beside <see cref="WorkItemKind.Epic"/>. Its fate is an open product
/// question, not this namespace's call.</para>
/// </summary>
public static class TrackerPriority
{
    /// <summary>
    /// The wire strings the tracker accepts for priority — exactly
    /// <see cref="TriagePriority"/>'s. Drift pin: a member added there flows
    /// through rather than diverging.
    /// </summary>
    public static readonly FrozenSet<string> AcceptedPriorityWires =
        Enum.GetValues<TriagePriority>().Select(p => p.ToWire()).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The wire strings the tracker accepts for item type — exactly
    /// <see cref="TriageIssueType"/>'s.
    /// </summary>
    public static readonly FrozenSet<string> AcceptedTypeWires =
        Enum.GetValues<TriageIssueType>().Select(t => t.ToWire()).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The sort key for an unset priority: <see cref="int.MaxValue"/>, so unset
    /// sorts after <see cref="TriagePriority.Low"/>.
    /// </summary>
    public const int UnsetSortKey = int.MaxValue;

    /// <summary>
    /// Parse a priority wire string, folding the documented
    /// <c>critical</c>→<c>urgent</c> / <c>medium</c>→<c>normal</c> aliases via
    /// <see cref="TriageVocabulary"/>.
    /// </summary>
    public static bool TryParsePriority(string? raw, out TriagePriority value) =>
        TriageVocabulary.TryParsePriority(raw, out value);

    /// <summary>Parse an item-type wire string via <see cref="TriageVocabulary"/>.</summary>
    public static bool TryParseType(string? raw, out TriageIssueType value) =>
        TriageVocabulary.TryParseType(raw, out value);

    /// <summary>
    /// The single definition of the priority sort rule (44-0 AC11): the
    /// declaration ordinal for a set priority, <see cref="UnsetSortKey"/> for
    /// null — so boards sort <c>urgent, high, normal, low, (unset)</c>.
    /// Call this rather than re-deriving the rule per query.
    /// </summary>
    public static int SortKey(TriagePriority? priority) =>
        priority is null ? UnsetSortKey : (int)priority.Value;
}
