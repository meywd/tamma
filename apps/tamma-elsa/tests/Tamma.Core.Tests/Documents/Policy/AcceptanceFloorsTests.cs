using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Documents.Policy;

/// <summary>
/// Epic-43 carried defect CD-1 / Story 43-0 amendment A1, closed 2026-07-30 —
/// the ONE non-wholesale rule in acceptance-rules resolution, tested purely:
/// a shipped human-acceptance requirement is a FLOOR, composed by <c>max()</c>
/// over a two-element lattice, and a tier that cannot name a document type may
/// not lower it.
///
/// <para>Story 43-16 (form α): the floor's VALUE is now DERIVED — Human while the
/// base-row dial is below the type's catalog level, Any at or above. These tests
/// derive their pivot dials from the actual catalog level, so they stay honest
/// whether the three human-pinned types sit at 101 (pre-remap) or 45 (post-remap).</para>
/// </summary>
[TestFixture]
public class AcceptanceFloorsTests
{
    private static int LevelOf(DocumentTypeKey type) =>
        ActionCatalog.Get(new ActionKey(ActionNamespace.DocumentType, type.ToWire())).DefaultMinAutonomy;

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

    /// <summary>
    /// The derived floor: for the three human-pinned types, a dial one below the
    /// type's catalog level floors Human, and a dial AT the level floors Any. The
    /// pivot is read from the catalog, so this holds at 101 (pre-remap) and at 45
    /// (post-remap) alike. <c>Findings</c> is the same shape from its own level.
    /// </summary>
    [Test]
    public void TheShippedFloor_IsDerivedFromTheCatalogLevel()
    {
        foreach (var type in new[]
                 {
                     DocumentTypeKey.Design, DocumentTypeKey.SprintPlan,
                     DocumentTypeKey.ThreatModel, DocumentTypeKey.Findings,
                 })
        {
            var level = LevelOf(type);
            AcceptanceFloors.ShippedFloorFor(type, level - 1)
                .Should().Be(AcceptorRequirement.Human, $"{type.ToWire()} at dial {level - 1} (< {level})");
            AcceptanceFloors.ShippedFloorFor(type, level)
                .Should().Be(AcceptorRequirement.Any, $"{type.ToWire()} at dial {level} (>= {level})");
        }
    }

    [Test]
    public void ApplyShippedAcceptorFloor_RaisesAny_ToAHumanPinnedTypesFloor()
    {
        // A dial below design's level forces the derived Human floor.
        var raised = AcceptanceFloors.ApplyShippedAcceptorFloor(
            Resolved(AcceptorRequirement.Any), DocumentTypeKey.Design, LevelOf(DocumentTypeKey.Design) - 1);

        raised.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human);
        raised.AcceptorRequirementFloored.Should().BeTrue(
            "the one non-wholesale field must be VISIBLE, not surprising");
        raised.Source.Should().Be(AcceptanceRulesSource.PrincipalDefault,
            "provenance still names the row that supplied every OTHER field — the "
            + "resolution stays wholesale apart from this one raise");
    }

    [Test]
    public void ApplyShippedAcceptorFloor_UsesTheSuppliedBaseDial_NotTheResolvedRowsOwnLevel()
    {
        // Story 43-16 AC3 / 43-11 Amendment 2-G — THE load-bearing caveat: the
        // floor derives from the BASE row's dial, passed explicitly, NEVER from the
        // resolved row's own AutonomyLevel. Here the resolved row carries a HIGH
        // autonomy (100 ≥ design's level 45 → would floor Any), but the supplied
        // base dial is below the level → the floor is Human. A wrong-wired
        // implementation that read resolved.Rules.AutonomyLevel returns Any and
        // goes red; removing the explicit baseDial parameter re-creates exactly
        // that wiring — a per-type autonomy edit would then silently move the
        // acceptor, which this pins against.
        var level = LevelOf(DocumentTypeKey.Design);
        var rowWithHighOwnDial = new ResolvedAcceptanceRules(
            AcceptanceDefaults.Rules with
            {
                AutonomyLevel = AutonomyDial.Max,
                AcceptorRequirement = AcceptorRequirement.Any,
            },
            AcceptanceRulesSource.PrincipalDefault, 3, "design", DateTimeOffset.UtcNow);

        var floored = AcceptanceFloors.ApplyShippedAcceptorFloor(
            rowWithHighOwnDial, DocumentTypeKey.Design, baseDial: level - 1);

        floored.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
            "the floor uses the SUPPLIED base dial (below the level → Human), not the row's "
            + "own AutonomyLevel (100 → would be Any)");
        floored.AcceptorRequirementFloored.Should().BeTrue();
    }

    [Test]
    public void ApplyShippedAcceptorFloor_IsANoOp_AtOrAboveTheTypesLevel()
    {
        // At a dial AT/above the type's level, the derived floor is Any → no raise.
        var input = Resolved(AcceptorRequirement.Any);

        var result = AcceptanceFloors.ApplyShippedAcceptorFloor(
            input, DocumentTypeKey.Design, LevelOf(DocumentTypeKey.Design));

        result.Should().BeSameAs(input);
        result.AcceptorRequirementFloored.Should().BeFalse();
    }

    [Test]
    public void ApplyShippedAcceptorFloor_NeverLowers_AndDoesNotFlagAnAlreadyHumanRow()
    {
        var alreadyHuman = Resolved(AcceptorRequirement.Human);

        // A row that is already at (or above) the floor is untouched — including
        // at a dial where the derived floor would be Any, where the row's own
        // `human` stands.
        AcceptanceFloors.ApplyShippedAcceptorFloor(
                alreadyHuman, DocumentTypeKey.Design, LevelOf(DocumentTypeKey.Design) - 1)
            .Should().BeSameAs(alreadyHuman);
        var atOrAbove = AcceptanceFloors.ApplyShippedAcceptorFloor(
            alreadyHuman, DocumentTypeKey.Design, LevelOf(DocumentTypeKey.Design));
        atOrAbove.Rules.AcceptorRequirement.Should().Be(AcceptorRequirement.Human,
            "the floor RAISES; it never pulls a tier's own tightening back down");
        atOrAbove.AcceptorRequirementFloored.Should().BeFalse();
    }

    /// <summary>
    /// The invariant, swept over the whole taxonomy AND every valid dial: applying
    /// the floor can only ever move a resolution up the lattice, for every document
    /// type, every starting value, and every dial position.
    /// </summary>
    [Test]
    public void TheFloor_IsMonotone_ForEveryDocumentTypeAtEveryDial()
    {
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
        {
            foreach (var dial in AutonomyDial.ValidLevels())
            {
                foreach (var start in Enum.GetValues<AcceptorRequirement>())
                {
                    var result = AcceptanceFloors.ApplyShippedAcceptorFloor(Resolved(start), type, dial);

                    ((int)result.Rules.AcceptorRequirement).Should().BeGreaterThanOrEqualTo(
                        (int)start, $"{type.ToWire()} at dial {dial} must never LOWER a stated requirement");
                    ((int)result.Rules.AcceptorRequirement).Should().BeGreaterThanOrEqualTo(
                        (int)AcceptanceFloors.ShippedFloorFor(type, dial),
                        $"{type.ToWire()}'s shipped floor at dial {dial} must survive every tier");
                }
            }
        }
    }
}
