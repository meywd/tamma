using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC8 — the freeze rule and its one sanctioned exception. The key
/// is minted once and never re-minted (including on a project move); a
/// deliberate operator re-key appends the outgoing key to <c>PreviousKeys</c>
/// and lookup resolves current-or-previous.
/// </summary>
[TestFixture]
public class WorkItemKeyHistoryTests
{
    [Test]
    public void Project_move_does_not_change_the_key()
    {
        // The freeze rule (D7/D13): a move to another project re-mints NOTHING.
        // The ref type offers no re-mint API — the key an item is created with
        // is the key it keeps, so after a move the prefix no longer matches the
        // project. That is intended: the key is already in
        // DocumentInstance.IssueId and DCB tags.issueId, and event tags are
        // append-only, so a re-mint would orphan lineage and history.
        var key = new WorkItemRef("TAM", 7);
        IReadOnlyList<string> previousKeys = [];

        // Simulate the move: the item's ProjectId changes (44-1's column, not
        // modeled here); the key and its history are untouched.
        key.ToWire().Should().Be("TAM-7", "the key is frozen at creation");
        previousKeys.Should().BeEmpty("a move alone records nothing — the key did not change");

        // And lookup by the frozen key still resolves the item.
        WorkItemKeyHistory.Matches(new WorkItemRef("TAM", 7), key, previousKeys).Should().BeTrue();
    }

    [Test]
    public void Rekey_records_and_resolves()
    {
        // The sanctioned exception: an operator renames the project prefix
        // TAM → TAMMA. The outgoing key is recorded; lookup resolves both.
        var outgoing = new WorkItemRef("TAM", 7);
        var current = new WorkItemRef("TAMMA", 7);

        var previous = WorkItemKeyHistory.Record([], outgoing);
        previous.Should().Equal("TAM-7");

        WorkItemKeyHistory.Matches(current, current, previous).Should().BeTrue("the current key resolves");
        WorkItemKeyHistory.Matches(outgoing, current, previous).Should().BeTrue("the previous key resolves");
        WorkItemKeyHistory.Matches(new WorkItemRef("TAM", 8), current, previous)
            .Should().BeFalse("an unrelated key resolves nothing");
    }

    [Test]
    public void Record_is_idempotent()
    {
        var outgoing = new WorkItemRef("TAM", 7);

        var once = WorkItemKeyHistory.Record([], outgoing);
        var twice = WorkItemKeyHistory.Record(once, outgoing);

        // (single-element Equal — the string params overload would treat a
        // 'because' argument as another expected element)
        twice.Should().Equal("TAM-7");
    }

    [Test]
    public void Record_preserves_order_oldest_first()
    {
        // Two successive re-keys: TAM → TAMMA → PLAT.
        var history = WorkItemKeyHistory.Record([], new WorkItemRef("TAM", 7));
        history = WorkItemKeyHistory.Record(history, new WorkItemRef("TAMMA", 7));

        history.Should().Equal("TAM-7", "TAMMA-7");

        // Every previous key still resolves, plus the current one.
        var current = new WorkItemRef("PLAT", 7);
        WorkItemKeyHistory.Matches(new WorkItemRef("TAM", 7), current, history).Should().BeTrue();
        WorkItemKeyHistory.Matches(new WorkItemRef("TAMMA", 7), current, history).Should().BeTrue();
        WorkItemKeyHistory.Matches(current, current, history).Should().BeTrue();
    }

    [Test]
    public void Record_does_not_mutate_its_input()
    {
        var original = new List<string> { "TAM-7" };
        var result = WorkItemKeyHistory.Record(original, new WorkItemRef("TAMMA", 7));

        original.Should().Equal("TAM-7"); // Record returns a new list, never mutates
        result.Should().Equal("TAM-7", "TAMMA-7");
    }
}
