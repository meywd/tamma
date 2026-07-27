using Tamma.Api.Services.Agents;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC3 — the closed grouping vocabulary over
/// <see cref="WorkItemStatus"/>. Count-pinned at 6 by
/// <c>WorkItemStatusCategoryTests</c>.
///
/// <para>Three statuses (<c>in_progress</c>, <c>in_review</c>, <c>blocked</c>)
/// are the same fact under three names — an item somebody has started. This
/// enum, via <see cref="WorkItemStatusCategoryExtensions.Category"/>, is the
/// <b>single definition of grouping</b>: 44-3, 44-4, 44-6, 44-7 and 44-9 must
/// call it rather than write status set literals, which drift.</para>
///
/// <para>Spelling: <c>cancelled</c>, matching <see cref="WorkItemStatus.Cancelled"/>
/// and the rest of this repo. Linear spells the equivalent category
/// <c>canceled</c>; the divergence is deliberate, not a mapping error.</para>
///
/// <para>This vocabulary is also the seam the eventual named-status-rows design
/// (per-project status rows carrying a closed category — deferred, see the epic
/// README's D11) grows through: when those rows arrive the grouping contract is
/// unchanged and no <c>Category()</c> caller is rewritten.</para>
/// </summary>
public enum WorkItemStatusCategory
{
    [Wire("triage")] Triage,
    [Wire("backlog")] Backlog,
    [Wire("unstarted")] Unstarted,
    [Wire("started")] Started,
    [Wire("completed")] Completed,
    [Wire("cancelled")] Cancelled,
}

public static class WorkItemStatusCategoryExtensions
{
    /// <summary>The canonical wire string for <paramref name="category"/>.</summary>
    public static string ToWire(this WorkItemStatusCategory category) =>
        EnumWire<WorkItemStatusCategory>.ToWire(category);

    /// <summary>Case-sensitive (ordinal) lookup of the member for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out WorkItemStatusCategory category) =>
        EnumWire<WorkItemStatusCategory>.TryParse(wire, out category);

    /// <summary>
    /// The category of <paramref name="status"/> — total over every
    /// <see cref="WorkItemStatus"/> member, defined ONCE here (Story 44-0 AC3).
    ///
    /// <para>Deliberately a <c>switch</c> expression with <b>no default arm</b>:
    /// adding a status without assigning it a category must be a compile-time
    /// diagnostic (CS8509), not a runtime surprise. Do not "tidy" this by adding
    /// a discard arm, and note that <c>unstarted</c> and <c>backlog</c> each
    /// having exactly one member is deliberate — the category is a contract,
    /// not a compression.</para>
    /// </summary>
#pragma warning disable CS8524 // Unnamed enum values ((WorkItemStatus)99) are unrepresentable here: every
    // construction path goes through EnumWire parsing or the enum literals, and an
    // unnamed value at runtime still fails loud (SwitchExpressionException).
    // CS8509 (a *named* member without an arm) stays ENABLED — it is the
    // compile-time guarantee this switch exists to provide (AC3).
    public static WorkItemStatusCategory Category(this WorkItemStatus status) => status switch
    {
        WorkItemStatus.Triage => WorkItemStatusCategory.Triage,
        WorkItemStatus.Backlog => WorkItemStatusCategory.Backlog,
        WorkItemStatus.Ready => WorkItemStatusCategory.Unstarted,
        WorkItemStatus.InProgress => WorkItemStatusCategory.Started,
        WorkItemStatus.InReview => WorkItemStatusCategory.Started,
        WorkItemStatus.Blocked => WorkItemStatusCategory.Started,
        WorkItemStatus.Done => WorkItemStatusCategory.Completed,
        WorkItemStatus.Cancelled => WorkItemStatusCategory.Cancelled,
    };
#pragma warning restore CS8524
}
