using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC14 — the relation vocabulary and its direction convention:
/// <c>blocks</c> directed source→target; <c>duplicate</c>/<c>related</c>
/// symmetric, canonicalized lower-id-first so a mirror edge cannot exist twice.
/// The edge table is 44-1's; validation beyond the pure convention is 44-3's.
/// </summary>
[TestFixture]
public class WorkItemRelationKindTests
{
    [Test]
    public void Member_count_and_roundtrip()
    {
        Enum.GetValues<WorkItemRelationKind>().Should().HaveCount(3);
        Enum.GetValues<WorkItemRelationKind>().Select(k => k.ToWire()).Should().Equal(
            "blocks", "duplicate", "related");

        foreach (var kind in Enum.GetValues<WorkItemRelationKind>())
        {
            WorkItemRelationKindExtensions.TryParse(kind.ToWire(), out var parsed).Should().BeTrue();
            parsed.Should().Be(kind);
            WorkItemRelationKindExtensions.Parse(kind.ToWire()).Should().Be(kind);
        }

        WorkItemRelationKindExtensions.TryParse("Blocks", out _).Should().BeFalse("ordinal parsing");

        var act = () => WorkItemRelationKindExtensions.Parse("depends_on");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("TRACKER.UNKNOWN_RELATION_KIND");
    }

    [Test]
    public void Blocks_is_the_only_directed_kind()
    {
        WorkItemRelationKind.Blocks.IsSymmetric().Should().BeFalse("'source blocks target' has a direction");
        WorkItemRelationKind.Duplicate.IsSymmetric().Should().BeTrue();
        WorkItemRelationKind.Related.IsSymmetric().Should().BeTrue();
    }

    [Test]
    public void Self_relation_is_rejected_for_every_kind()
    {
        var id = Guid.NewGuid();

        foreach (var kind in Enum.GetValues<WorkItemRelationKind>())
        {
            var act = () => kind.Canonicalize(id, id);
            act.Should().Throw<TammaError>(because: $"a work item cannot be '{kind.ToWire()}' with itself")
                .Which.Code.Should().Be("TRACKER.SELF_RELATION");
        }
    }

    [Test]
    public void Symmetric_edges_canonicalize_to_one_storable_form()
    {
        // The dedup rule: a symmetric edge inserted as (a,b) and as (b,a) must
        // land on the same row, so 44-1's unique index can reject the mirror.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        foreach (var kind in new[] { WorkItemRelationKind.Duplicate, WorkItemRelationKind.Related })
        {
            var forward = kind.Canonicalize(a, b);
            var mirrored = kind.Canonicalize(b, a);

            mirrored.Should().Be(forward, because: $"'{kind.ToWire()}' is symmetric — one storable form");

            // Lower id first, by Guid.CompareTo.
            forward.SourceId.CompareTo(forward.TargetId).Should().BeNegative();
        }
    }

    [Test]
    public void Blocks_preserves_direction()
    {
        // 'A blocks B' and 'B blocks A' are different facts — canonicalization
        // must never swap a directed edge's endpoints.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        WorkItemRelationKind.Blocks.Canonicalize(a, b).Should().Be((a, b));
        WorkItemRelationKind.Blocks.Canonicalize(b, a).Should().Be((b, a));
    }
}
