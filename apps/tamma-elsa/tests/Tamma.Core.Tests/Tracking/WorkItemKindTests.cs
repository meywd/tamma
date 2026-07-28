using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Types;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC1 — the closed 4-member kind vocabulary. Count pin + wire
/// round-trip per <c>TriageDecisionTypeTests</c>; the bug/chore deletion is
/// asserted, not assumed.
/// </summary>
[TestFixture]
public class WorkItemKindTests
{
    [Test]
    public void Member_count_is_pinned()
    {
        Enum.GetValues<WorkItemKind>().Should().HaveCount(4);

        // The exact wire strings 44-1's ck_work_items_kind CHECK must mirror.
        Enum.GetValues<WorkItemKind>().Select(k => k.ToWire())
            .Should().Equal("epic", "story", "task", "spike");
    }

    [Test]
    public void Roundtrip_holds_for_every_member()
    {
        foreach (var kind in Enum.GetValues<WorkItemKind>())
        {
            WorkItemKindExtensions.TryParse(kind.ToWire(), out var parsed)
                .Should().BeTrue(because: $"'{kind.ToWire()}' must round-trip");
            parsed.Should().Be(kind);
            WorkItemKindExtensions.Parse(kind.ToWire()).Should().Be(kind);
        }

        // Ordinal, case-sensitive: non-canonical casing is rejected, not coerced.
        WorkItemKindExtensions.TryParse("Epic", out _).Should().BeFalse();
        WorkItemKindExtensions.TryParse("EPIC", out _).Should().BeFalse();
    }

    [Test]
    public void Unknown_kind_fails_loud()
    {
        var act = () => WorkItemKindExtensions.Parse("bogus");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.UNKNOWN_KIND");
    }

    [Test]
    public void Bug_and_chore_are_not_kinds()
    {
        // The AC1 deletion, asserted: bug/chore live on the TriageIssueType
        // (type) axis only. (Kind=Bug, Type=Feature) must be unrepresentable.
        WorkItemKindExtensions.TryParse("bug", out _).Should().BeFalse();
        WorkItemKindExtensions.TryParse("chore", out _).Should().BeFalse();

        EnumWire<TriageIssueType>.TryParse("bug", out var bug).Should().BeTrue();
        bug.Should().Be(TriageIssueType.Bug);
        EnumWire<TriageIssueType>.TryParse("chore", out var chore).Should().BeTrue();
        chore.Should().Be(TriageIssueType.Chore);
    }
}
