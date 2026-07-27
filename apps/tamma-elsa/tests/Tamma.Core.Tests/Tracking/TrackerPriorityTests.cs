using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Types;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC11/AC12 — the tracker's binding to the shipped triage
/// vocabularies: a drift pin, an ordinal pin (the thing <c>[Wire]</c> does not
/// guarantee), and the nullable-priority sort rule.
/// </summary>
[TestFixture]
public class TrackerPriorityTests
{
    [Test]
    public void Accepted_wires_equal_the_triage_vocabularies()
    {
        // Drift pin (D8): the tracker accepts exactly TriagePriority's and
        // TriageIssueType's wire sets — a member added there flows through
        // rather than diverging. No new enum, no fifth priority vocabulary.
        TrackerPriority.AcceptedPriorityWires
            .Should().BeEquivalentTo(Enum.GetValues<TriagePriority>().Select(p => p.ToWire()));
        TrackerPriority.AcceptedTypeWires
            .Should().BeEquivalentTo(Enum.GetValues<TriageIssueType>().Select(t => t.ToWire()));

        // And the current literal contents, so a triage-side edit is visible here too.
        TrackerPriority.AcceptedPriorityWires.Should().BeEquivalentTo("urgent", "high", "normal", "low");
        TrackerPriority.AcceptedTypeWires.Should().BeEquivalentTo(
            "bug", "feature", "chore", "question", "security", "docs");
    }

    [Test]
    public void TriagePriority_ordinals_are_pinned()
    {
        // D12: [Wire] pins strings, not declaration order — yet every
        // priority-sorted board and ORDER BY in 44-3/44-4/44-7 rests on these
        // ordinals. A well-meaning alphabetisation would silently invert them.
        ((int)TriagePriority.Urgent).Should().Be(0);
        ((int)TriagePriority.High).Should().Be(1);
        ((int)TriagePriority.Normal).Should().Be(2);
        ((int)TriagePriority.Low).Should().Be(3);
    }

    [Test]
    public void Unset_priority_sorts_after_low()
    {
        // AC11: null ("nobody has prioritised this") is a different fact from
        // normal ("somebody looked and said normal") — and it sorts last.
        TrackerPriority.SortKey(null).Should().Be(TrackerPriority.UnsetSortKey);
        TrackerPriority.SortKey(null).Should().BeGreaterThan(TrackerPriority.SortKey(TriagePriority.Low));

        var boardOrder = new TriagePriority?[] { null, TriagePriority.Normal, TriagePriority.Urgent, TriagePriority.Low, TriagePriority.High }
            .OrderBy(TrackerPriority.SortKey)
            .ToArray();

        boardOrder.Should().Equal(
            TriagePriority.Urgent, TriagePriority.High, TriagePriority.Normal, TriagePriority.Low, null);
    }

    [Test]
    public void Aliases_still_fold()
    {
        // The documented critical→urgent / medium→normal synonyms keep working
        // through the tracker's parse surface (AC12).
        TrackerPriority.TryParsePriority("critical", out var urgent).Should().BeTrue();
        urgent.Should().Be(TriagePriority.Urgent);

        TrackerPriority.TryParsePriority("medium", out var normal).Should().BeTrue();
        normal.Should().Be(TriagePriority.Normal);

        TrackerPriority.TryParsePriority("P0", out _).Should().BeFalse("the prompt's P0..P3 vocabulary is not adopted");

        TrackerPriority.TryParseType("bug", out var bug).Should().BeTrue();
        bug.Should().Be(TriageIssueType.Bug);
        TrackerPriority.TryParseType("enhancement", out _).Should().BeFalse();
    }
}
