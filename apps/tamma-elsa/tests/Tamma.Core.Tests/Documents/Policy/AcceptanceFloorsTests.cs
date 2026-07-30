using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Epic-43 carried defect CD-1 / Story 43-0 amendment A1, closed 2026-07-30 —
/// the ONE non-wholesale rule in acceptance-rules resolution, tested purely:
/// a shipped human-acceptance requirement is a FLOOR, composed by <c>max()</c>
/// over a two-element lattice, and a tier that cannot name a document type may
/// not lower it.
/// </summary>
[TestFixture]
public class AcceptanceFloorsTests
{
    private static ResolvedAcceptanceRules Resolved(
        AcceptorRequirement requirement,
        AcceptanceRulesSource source = AcceptanceRulesSource.PrincipalDefault)
        => new(
            AcceptanceDefaults.Rules with { AcceptorRequirement = requirement },
            source, 3, "design", DateTimeOffset.UtcNow);

    /// <summary>
    /// The lattice is the enum's declaration order, and <c>Any</c> being first is
    /// load-bearing elsewhere too (it is the CLR default, so a legacy row
    /// deserializes to today's behaviour). A member inserted out of order would
    /// silently break <c>max()</c>.
    /// </summary>
    [Test]
    public void TheLattice_IsAnyThenHuman_AndMaxOnlyTightens()
    {
        ((int)AcceptorRequirement.Any).Should().Be(0);
        ((int)AcceptorRequirement.Human).Should().BeGreaterThan((int)AcceptorRequirement.Any);

        AcceptanceFloors.Max(AcceptorRequirement.Any, AcceptorRequirement.Any)
            .Should().Be(AcceptorRequirement.Any);
        AcceptanceFloors.Max(AcceptorRequirement.Any, AcceptorRequirement.Human)
            .Should().Be(AcceptorRequirement.Human);
        AcceptanceFloors.Max(AcceptorRequirement.Human, AcceptorRequirement.Any)
            .Should().Be(AcceptorRequirement.Human);
    }

    /// <summary>The three shipped human-pinned types, named. A fourth added later
    /// is picked up automatically by the sweep below.</summary>
    [Test]
    public void TheShippedHumanFloor_CoversDesign_SprintPlan_AndThreatModel()
    {
        AcceptanceFloors.ShippedFloorFor(DocumentTypeKey.Design)
            .Should().Be(AcceptorRequirement.Human);
        AcceptanceFloors.ShippedFloorFor(DocumentTypeKey.SprintPlan)
            .Should().Be(AcceptorRequirement.Human);
        AcceptanceFloors.ShippedFloorFor(DocumentTypeKey.ThreatModel)
            .Should().Be(AcceptorRequirement.Human);
        AcceptanceFloors.ShippedFloorFor(DocumentTypeKey.Findings)
            .Should().Be(AcceptorRequirement.Any);
    }

    [Test]
    public void ApplyShippedAcceptorFloor_RaisesAny_ToAHumanPinnedTypesFloor()
    {
        var raised = AcceptanceFloors.ApplyShippedAcceptorFloor(
            Resolved(AcceptorRequirement.Any), DocumentTypeKey.Design);

        raised.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
        raised.AcceptorRequirementFloored.Should().BeTrue(
            "the one non-wholesale field must be VISIBLE, not surprising");
        raised.Source.Should().Be(AcceptanceRulesSource.PrincipalDefault,
            "provenance still names the row that supplied every OTHER field — the "
            + "resolution stays wholesale apart from this one raise");
    }

    [Test]
    public void ApplyShippedAcceptorFloor_IsANoOp_OnATypeWithNoShippedFloor()
    {
        var input = Resolved(AcceptorRequirement.Any);

        var result = AcceptanceFloors.ApplyShippedAcceptorFloor(input, DocumentTypeKey.Findings);

        result.Should().BeSameAs(input);
        result.AcceptorRequirementFloored.Should().BeFalse();
    }

    [Test]
    public void ApplyShippedAcceptorFloor_NeverLowers_AndDoesNotFlagAnAlreadyHumanRow()
    {
        var alreadyHuman = Resolved(AcceptorRequirement.Human);

        // A row that is already at (or above) the floor is untouched — including
        // on a type with no shipped floor, where the row's own `human` stands.
        AcceptanceFloors.ApplyShippedAcceptorFloor(alreadyHuman, DocumentTypeKey.Design)
            .Should().BeSameAs(alreadyHuman);
        var onAnyType = AcceptanceFloors.ApplyShippedAcceptorFloor(
            alreadyHuman, DocumentTypeKey.Findings);
        onAnyType.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
            "the floor RAISES; it never pulls a tier's own tightening back down");
        onAnyType.AcceptorRequirementFloored.Should().BeFalse();
    }

    /// <summary>
    /// The invariant, swept over the whole taxonomy: applying the floor can only
    /// ever move a resolution up the lattice, for every document type and every
    /// starting value.
    /// </summary>
    [Test]
    public void TheFloor_IsMonotone_ForEveryDocumentType()
    {
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
        {
            foreach (var start in Enum.GetValues<AcceptorRequirement>())
            {
                var result = AcceptanceFloors.ApplyShippedAcceptorFloor(Resolved(start), type);

                ((int)result.Rules.AcceptorRequirement).Should().BeGreaterThanOrEqualTo(
                    (int)start, $"{type.ToWire()} must never LOWER a stated requirement");
                ((int)result.Rules.AcceptorRequirement).Should().BeGreaterThanOrEqualTo(
                    (int)AcceptanceFloors.ShippedFloorFor(type),
                    $"{type.ToWire()}'s shipped floor must survive every tier");
            }
        }
    }
}
