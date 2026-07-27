using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC3 — <c>WorkItemStatusCategory</c> and <c>Category()</c>, the
/// single definition of grouping. The full status→category table is pinned
/// literally; <c>IsTerminal</c> is derived, never a second hand-maintained set.
/// </summary>
[TestFixture]
public class WorkItemStatusCategoryTests
{
    [Test]
    public void Member_count_is_pinned()
    {
        Enum.GetValues<WorkItemStatusCategory>().Should().HaveCount(6);

        // Spelling note: 'cancelled', matching the repo — Linear spells the
        // equivalent category 'canceled'; the divergence is deliberate.
        Enum.GetValues<WorkItemStatusCategory>().Select(c => c.ToWire()).Should().Equal(
            "triage", "backlog", "unstarted", "started", "completed", "cancelled");
    }

    [Test]
    public void Category_table_is_pinned()
    {
        // The full 8-row table, literally (AC3). Note: unstarted and backlog
        // each having exactly one member is deliberate — the category is a
        // contract, not a compression; do not "simplify" it away.
        WorkItemStatus.Triage.Category().Should().Be(WorkItemStatusCategory.Triage);
        WorkItemStatus.Backlog.Category().Should().Be(WorkItemStatusCategory.Backlog);
        WorkItemStatus.Ready.Category().Should().Be(WorkItemStatusCategory.Unstarted);
        WorkItemStatus.InProgress.Category().Should().Be(WorkItemStatusCategory.Started);
        WorkItemStatus.InReview.Category().Should().Be(WorkItemStatusCategory.Started);
        WorkItemStatus.Blocked.Category().Should().Be(WorkItemStatusCategory.Started);
        WorkItemStatus.Done.Category().Should().Be(WorkItemStatusCategory.Completed);
        WorkItemStatus.Cancelled.Category().Should().Be(WorkItemStatusCategory.Cancelled);
    }

    [Test]
    public void Category_is_total_over_every_status()
    {
        foreach (var status in Enum.GetValues<WorkItemStatus>())
        {
            var act = () => status.Category();
            act.Should().NotThrow(because: $"Category() must be total; '{status.ToWire()}' has no category");
        }
    }

    [Test]
    public void Every_category_is_reachable()
    {
        // An unreachable category is a vocabulary bug (AC3).
        var reachable = Enum.GetValues<WorkItemStatus>().Select(s => s.Category()).ToHashSet();
        reachable.Should().BeEquivalentTo(Enum.GetValues<WorkItemStatusCategory>());
    }

    [Test]
    public void IsTerminal_is_derived_from_category()
    {
        foreach (var status in Enum.GetValues<WorkItemStatus>())
        {
            var expected = status.Category() is WorkItemStatusCategory.Completed or WorkItemStatusCategory.Cancelled;
            status.IsTerminal().Should().Be(expected, because: $"IsTerminal must be derived for '{status.ToWire()}'");
        }

        // And pinned concretely: exactly done + cancelled are terminal.
        Enum.GetValues<WorkItemStatus>().Where(s => s.IsTerminal())
            .Should().Equal(WorkItemStatus.Done, WorkItemStatus.Cancelled);
    }

    [Test]
    public void Category_wires_roundtrip()
    {
        foreach (var category in Enum.GetValues<WorkItemStatusCategory>())
        {
            WorkItemStatusCategoryExtensions.TryParse(category.ToWire(), out var parsed).Should().BeTrue();
            parsed.Should().Be(category);
        }

        WorkItemStatusCategoryExtensions.TryParse("Started", out _).Should().BeFalse();
        // Linear's spelling must NOT parse — one word, one spelling.
        WorkItemStatusCategoryExtensions.TryParse("canceled", out _).Should().BeFalse();
    }
}
