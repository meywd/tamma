using System.Collections.Frozen;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC4–AC6 — the tracker's structural hierarchy invariants.
/// <b>Not a <c>(parentKind, childKind)</c> whitelist</b>; that matrix was
/// deleted in the story's v2 rework and must not be reinstated. It had three
/// distinct rows — it encoded <em>level</em> (root/branch/leaf) while presenting
/// as a rule over <em>kind</em> — and what it forbade was ordinary work: a task
/// directly under a small epic, a sub-spike, and decomposing a task at all
/// (the shipped <c>Epic → Story → DecompositionTask → PlanTask</c> chain is
/// depth 4 against the old <c>MaxDepth = 3</c>). Under a closed vocabulary an
/// agent's worst case is "picked the wrong member" — recoverable; under a closed
/// parenting matrix it is "produced a correct decomposition the matrix rejects",
/// recoverable only by fabricating structure. Rejecting a valid plan costs more
/// than mislabelling one. Full evidence:
/// <c>.dev/findings/linear-comparison-against-story-44-0.md</c>.
///
/// <para>The remaining invariants are: no cycles; depth ≤ <see cref="MaxDepth"/>;
/// and exactly one kind rule — <em>an Epic may not be a child of a
/// non-Epic</em> (Epic-under-Epic IS allowed: sub-epics). The first two need a
/// parent chain and therefore I/O, so 44-3's service enforces them against the
/// constants stated here; the kind rule is <see cref="CanParent"/>.</para>
///
/// <para>The per-kind rule index is declared in <c>s_rules</c> and <b>built</b>
/// (the <c>RolePhaseMap</c> idiom): a <see cref="WorkItemKind"/> without a row
/// throws <see cref="TrackerVocabularyException"/> from the static initializer,
/// so adding a fifth kind without deciding its rule stays a boot failure.</para>
/// </summary>
public static class TrackerHierarchy
{
    /// <summary>
    /// Maximum hierarchy depth, enforced by 44-3's service (evaluating depth
    /// needs the parent chain, which needs I/O — this type stays pure).
    /// Six, not three: the shipped <c>Epic → Story → DecompositionTask →
    /// PlanTask</c> chain is already depth 4, and a bound below the data is not
    /// a bound, it is a future migration. Six still keeps the recursive CTE
    /// fixed-cost and the board render unambiguous, which is all the bound is
    /// for.
    /// </summary>
    public const int MaxDepth = 6;

    /// <summary>
    /// One row per <see cref="WorkItemKind"/>. The row is the kind's structural
    /// invariant (may it parent an Epic?), not its child set. A missing row is a
    /// <see cref="TrackerVocabularyException"/> at first touch.
    /// </summary>
    private static readonly (WorkItemKind Kind, bool MayParentAnEpic)[] s_rules =
    [
        (WorkItemKind.Epic, true),
        (WorkItemKind.Story, false),
        (WorkItemKind.Task, false),
        (WorkItemKind.Spike, false),
    ];

    private static readonly FrozenDictionary<WorkItemKind, bool> s_mayParentAnEpic = Build(s_rules);

    /// <summary>
    /// Pure core: validate + index a rule set. Split from the static
    /// initializer so tests can drive it with a synthetic (incomplete) rule set
    /// — the <c>PromptFileLoader.Build</c> shape.
    /// </summary>
    /// <exception cref="TrackerVocabularyException">
    /// A <see cref="WorkItemKind"/> has no rule row, or a kind has two rows.
    /// The exception names the offending member.
    /// </exception>
    internal static FrozenDictionary<WorkItemKind, bool> Build(
        IReadOnlyList<(WorkItemKind Kind, bool MayParentAnEpic)> rules)
    {
        var index = new Dictionary<WorkItemKind, bool>();
        foreach (var (kind, mayParentAnEpic) in rules)
        {
            if (!index.TryAdd(kind, mayParentAnEpic))
            {
                throw new TrackerVocabularyException(
                    kind.ToString(),
                    $"TrackerHierarchy declares two rules for WorkItemKind.{kind}. " +
                    "Exactly one structural-invariant row per kind is required.");
            }
        }

        foreach (var kind in Enum.GetValues<WorkItemKind>())
        {
            if (!index.ContainsKey(kind))
            {
                throw new TrackerVocabularyException(
                    kind.ToString(),
                    $"TrackerHierarchy has no structural-invariant rule for WorkItemKind.{kind}. " +
                    "Every kind must declare its rule; deciding it is part of adding the member.");
            }
        }

        return index.ToFrozenDictionary();
    }

    /// <summary>
    /// The single kind rule: an Epic may not be a child of a non-Epic. Every
    /// other <c>(parent, child)</c> pair is permitted — including
    /// Epic-under-Epic (sub-epics) and Task-under-Epic (the case the deleted
    /// matrix forbade).
    ///
    /// <para>A <c>null</c> parent is <b>always</b> permitted and the rule index
    /// is not consulted (Story 44-0 AC6): any kind, <c>task</c> included, may be
    /// top-level. Otherwise an imported or triaged item could not exist until
    /// somebody invented a parent epic for it.</para>
    /// </summary>
    public static bool CanParent(WorkItemKind? parent, WorkItemKind child)
    {
        if (parent is null)
            return true;

        return child != WorkItemKind.Epic || s_mayParentAnEpic[parent.Value];
    }

    /// <summary>
    /// ADVISORY ONLY — "where the UI puts it when nobody said". Consumed by
    /// 44-6's create form and 44-8's import defaults; NEVER by a validator
    /// (Story 44-0 AC6). True for <see cref="WorkItemKind.Epic"/> only.
    /// </summary>
    public static bool IsDefaultRoot(WorkItemKind kind) => kind == WorkItemKind.Epic;
}
