using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC4–AC6 — structural invariants, not a parenting matrix. The
/// fail-loud built index survives the matrix's deletion; test names are chosen
/// so a reinstatement of the whitelist fails loudly.
/// </summary>
[TestFixture]
public class TrackerHierarchyTests
{
    [Test]
    public void Every_kind_has_a_rule()
    {
        // First touch of the type would throw TypeInitializationException if a
        // kind lacked its structural-invariant row; consulting the index for
        // every kind proves the built index is defined for all 4.
        foreach (var parent in Enum.GetValues<WorkItemKind>())
        {
            var act = () => TrackerHierarchy.CanParent(parent, WorkItemKind.Epic);
            act.Should().NotThrow(because: $"'{parent}' must have a structural-invariant rule");
        }
    }

    [Test]
    public void Epic_may_not_be_a_child_of_a_non_epic()
    {
        // The single kind rule, over all 16 (parent, child) pairs: only
        // (Story|Task|Spike, Epic) is rejected. Epic-under-Epic IS allowed
        // (sub-epics).
        foreach (var parent in Enum.GetValues<WorkItemKind>())
        {
            foreach (var child in Enum.GetValues<WorkItemKind>())
            {
                var expected = child != WorkItemKind.Epic || parent == WorkItemKind.Epic;
                TrackerHierarchy.CanParent(parent, child).Should().Be(
                    expected, because: $"CanParent({parent}, {child}) must be {expected}");
            }
        }
    }

    [Test]
    public void Task_under_epic_is_permitted()
    {
        // The case the deleted (parentKind, childKind) matrix forbade — named
        // so a reinstatement of the matrix fails loudly (AC4/D4). An agent
        // decomposing a small epic into tasks must not be forced to fabricate a
        // filler story.
        TrackerHierarchy.CanParent(WorkItemKind.Epic, WorkItemKind.Task).Should().BeTrue();

        // The other decompositions the matrix forbade, also permitted now:
        TrackerHierarchy.CanParent(WorkItemKind.Spike, WorkItemKind.Spike).Should().BeTrue("sub-spikes are ordinary work");
        TrackerHierarchy.CanParent(WorkItemKind.Task, WorkItemKind.Task).Should().BeTrue(
            "decomposing a task is shipped reality (DecompositionTask → PlanTask)");
        TrackerHierarchy.CanParent(WorkItemKind.Epic, WorkItemKind.Epic).Should().BeTrue("sub-epics are allowed");
    }

    [Test]
    public void Missing_rule_fails_loud()
    {
        // The pure Build core driven with a synthetic rule set missing a
        // member — the PromptFileLoader.Build assertion shape (AC16).
        var missingSpike = new[]
        {
            (WorkItemKind.Epic, true),
            (WorkItemKind.Story, false),
            (WorkItemKind.Task, false),
        };

        var act = () => TrackerHierarchy.Build(missingSpike);
        act.Should().Throw<TrackerVocabularyException>()
            .Which.MemberName.Should().Be(nameof(WorkItemKind.Spike));
    }

    [Test]
    public void Duplicate_rule_fails_loud()
    {
        var duplicated = new[]
        {
            (WorkItemKind.Epic, true),
            (WorkItemKind.Story, false),
            (WorkItemKind.Story, false),
            (WorkItemKind.Task, false),
            (WorkItemKind.Spike, false),
        };

        var act = () => TrackerHierarchy.Build(duplicated);
        act.Should().Throw<TrackerVocabularyException>()
            .Which.MemberName.Should().Be(nameof(WorkItemKind.Story));
    }

    [Test]
    public void Complete_rule_set_builds()
    {
        var complete = new[]
        {
            (WorkItemKind.Epic, true),
            (WorkItemKind.Story, false),
            (WorkItemKind.Task, false),
            (WorkItemKind.Spike, false),
        };

        var index = TrackerHierarchy.Build(complete);
        index.Should().HaveCount(4);
        index[WorkItemKind.Epic].Should().BeTrue();
        index[WorkItemKind.Story].Should().BeFalse();
    }

    [Test]
    public void MaxDepth_is_six()
    {
        // 44-3 enforces against this constant; it is pinned here so changing it
        // is a deliberate, test-visible edit.
        TrackerHierarchy.MaxDepth.Should().Be(6);

        // And it must clear the depth-4 chain the codebase already ships
        // (Epic → Story → DecompositionTask → PlanTask): a bound below the data
        // is not a bound, it is a future migration.
        TrackerHierarchy.MaxDepth.Should().BeGreaterThan(4);
    }

    [Test]
    public void IsDefaultRoot_is_advisory()
    {
        // "Where the UI puts it when nobody said" — Epic only, never a validator.
        TrackerHierarchy.IsDefaultRoot(WorkItemKind.Epic).Should().BeTrue();
        TrackerHierarchy.IsDefaultRoot(WorkItemKind.Story).Should().BeFalse();
        TrackerHierarchy.IsDefaultRoot(WorkItemKind.Task).Should().BeFalse();
        TrackerHierarchy.IsDefaultRoot(WorkItemKind.Spike).Should().BeFalse();
    }

    [Test]
    public void Any_kind_may_be_top_level()
    {
        // AC6: a null parent is always permitted — the rule index is not
        // consulted. Otherwise an imported GitHub issue or a triaged item could
        // not exist until somebody invented a parent epic for it (which would
        // break 44-8's bulk import and the whole triage path).
        foreach (var kind in Enum.GetValues<WorkItemKind>())
        {
            TrackerHierarchy.CanParent(null, kind).Should().BeTrue(
                because: $"a '{kind}' with no parent is ordinary, not an error");
        }
    }
}
