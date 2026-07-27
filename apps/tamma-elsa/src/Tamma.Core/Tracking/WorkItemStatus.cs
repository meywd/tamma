using Tamma.Api.Services.Agents;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC2 — the closed status vocabulary of a native work item.
/// Count-pinned at 8 by <c>WorkItemStatusTests</c>; the CHECK constraint
/// <c>ck_work_items_status</c> (created by 44-1 on <c>work_items</c>) must mirror
/// these exact eight wire strings:
/// <c>triage</c>, <c>backlog</c>, <c>ready</c>, <c>in_progress</c>,
/// <c>in_review</c>, <c>blocked</c>, <c>done</c>, <c>cancelled</c>.
/// Multi-word wires use <c>_</c>, exactly as
/// <see cref="Documents.DocumentInstanceStatus"/>'s <c>in_review</c> does.
///
/// <para><b><c>triage</c> is a member from day one.</b> 44-8 imports GitHub
/// issues and <c>FetchUntriagedItemsActivity</c> already exists, so items arrive
/// with nobody having looked at them. Without <c>triage</c> those merge into
/// <c>backlog</c>, conflating "we decided not now" with "nobody has decided".
/// Adding an enum member later is a migration over <c>ck_work_items_status</c>
/// on the highest-row-count tenant table, replayed across every tenant schema —
/// members are cheap now and expensive forever after.</para>
///
/// <para><b>Not <see cref="Documents.DocumentInstanceStatus"/>.</b> That
/// vocabulary describes a <em>revision's</em> review position
/// (<c>draft → … → accepted|rejected|superseded|escalated</c>); this one
/// describes a <em>thing to be done</em>. Merging them would put
/// <c>superseded</c> on a backlog board. The wire sets are deliberately
/// non-identical so a mistaken <c>TryParse</c> across them fails rather than
/// succeeding wrongly.</para>
///
/// <para>Grouping logic ("is it in flight?", "which board column group?") is
/// defined once, by <see cref="WorkItemStatusCategoryExtensions.Category"/> —
/// never as a set literal at a call site.</para>
/// </summary>
public enum WorkItemStatus
{
    [Wire("triage")] Triage,
    [Wire("backlog")] Backlog,
    [Wire("ready")] Ready,
    [Wire("in_progress")] InProgress,
    [Wire("in_review")] InReview,
    [Wire("blocked")] Blocked,
    [Wire("done")] Done,
    [Wire("cancelled")] Cancelled,
}

public static class WorkItemStatusExtensions
{
    /// <summary>The canonical wire string for <paramref name="status"/>.</summary>
    public static string ToWire(this WorkItemStatus status) => EnumWire<WorkItemStatus>.ToWire(status);

    /// <summary>Case-sensitive (ordinal) lookup of the member for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out WorkItemStatus status) =>
        EnumWire<WorkItemStatus>.TryParse(wire, out status);

    /// <summary>
    /// Resolve a wire string to a <see cref="WorkItemStatus"/> (case-sensitive, ordinal).
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>TRACKER.UNKNOWN_STATUS</c> for null, empty, or unknown input.
    /// </exception>
    public static WorkItemStatus Parse(string input)
    {
        if (!string.IsNullOrWhiteSpace(input) && EnumWire<WorkItemStatus>.TryParse(input, out var status))
            return status;

        throw new TammaError(
            "TRACKER.UNKNOWN_STATUS",
            $"Unknown work item status: '{input}'. Valid statuses: " +
            $"{string.Join(", ", Enum.GetValues<WorkItemStatus>().Select(s => s.ToWire()))}.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Whether <paramref name="status"/> is terminal — <b>derived</b> from
    /// <see cref="WorkItemStatusCategoryExtensions.Category"/>, never a second
    /// hand-maintained set (Story 44-0 AC3).
    /// </summary>
    public static bool IsTerminal(this WorkItemStatus status) =>
        status.Category() is WorkItemStatusCategory.Completed or WorkItemStatusCategory.Cancelled;
}
