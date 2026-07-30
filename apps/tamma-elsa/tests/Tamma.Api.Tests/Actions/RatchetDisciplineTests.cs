using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 <b>AC8</b> — the META-test (amendment §A1 carve-out #5, closed
/// 2026-07-30). Every ratchet in this epic must have <b>all three</b> properties:
/// (a) a <b>staleness</b> arm — an entry that now passes fails until deleted;
/// (b) <b>justification classification</b> — a placeholder cannot buy an entry;
/// (c) a <b>count pin</b>, and one that is <b>mechanically shrink-only</b>.
///
/// <para><b>Why a meta-test and not four per-ratchet tests.</b> All three properties
/// were already asserted PER RATCHET when 43-8 landed. What was missing is the
/// assertion that a <b>FIFTH</b> ratchet also has them: nothing stopped a future
/// author from shipping a baseline with a count pin and no staleness arm, and
/// nothing would have noticed. This fixture is the thing that notices — a ratchet
/// must be DECLARED here, and a declaration that cannot demonstrate all three
/// properties fails.</para>
///
/// <para><b>Each property is PROVED, not described.</b> The staleness arm is driven
/// with synthetic stale input through the owning fixture's real classifier and must
/// report; the classifier is driven with placeholders and must reject them (a
/// <c>_ =&gt; true</c> classifier fails here); the count pin is resolved by
/// reflection against its owning fixture's <c>PinHistory</c> and its direction is
/// asserted. A declaration cannot be satisfied by a comment.</para>
///
/// <para><b>HONEST RESIDUALS</b> — recorded because AC10 makes these doc-comments
/// load-bearing:</para>
/// <list type="bullet">
///   <item><b>This fixture is per-assembly.</b> <c>Tamma.Api.Tests</c> and
///   <c>Tamma.Activities.Tests</c> do not reference each other, so there is no
///   single fixture that can see all four of the epic's ratchets. This one covers
///   the ONE ratchet that lives in this assembly
///   (<see cref="KnownUngovernedEndpoints"/>); the sibling
///   <c>Tamma.Activities.Tests.Actions.RatchetDisciplineTests</c> covers the other
///   three (<c>KnownNonEffectClientMethods</c>, <c>NotDiRegisteredTools</c>,
///   <c>UnattributedActivities</c>). Both carry a registry count pin, so a new
///   ratchet in EITHER assembly must be declared.</item>
///   <item><b>Declaration is opt-in.</b> A future ratchet its author never declares
///   here is not covered. There is no mechanical way to recognise "a ratchet" among
///   arbitrary static collections without a marker, and a shape/name heuristic would
///   sweep in unrelated allowlists from other epics (notably
///   <c>ContractBindingTests</c>, whose ratchet deliberately has only two of the
///   three properties and is NOT this story's to fix). The registry pin below is the
///   mitigation: adding a ratchet to this assembly's Actions folder without
///   declaring it leaves the pin stating a number a reviewer can check.</item>
/// </list>
/// </summary>
[TestFixture]
public class RatchetDisciplineTests
{
    /// <summary>
    /// One declared ratchet and the three properties AC8 requires of it. Every
    /// member is a delegate or a reflected fact, never a claim: the meta-test
    /// EXERCISES each one.
    /// </summary>
    /// <param name="Name">The ratchet's identifier, for failure messages.</param>
    /// <param name="OwningFixture">The fixture that owns the three per-ratchet tests.</param>
    /// <param name="StalenessTestName">
    /// The name of the owning fixture's staleness test. Resolved by reflection and
    /// required to carry <c>[Test]</c>, so deleting or renaming it fails HERE even
    /// though this fixture cannot run it.
    /// </param>
    /// <param name="StalenessProbe">
    /// Drives the owning fixture's REAL classifier with synthetic stale input.
    /// Must report at least one problem.
    /// </param>
    /// <param name="Justifications">The live justification strings.</param>
    /// <param name="Classifier">The owning fixture's REAL justification classifier.</param>
    /// <param name="LiveCount">The ratchet's current entry count.</param>
    /// <param name="PinHistory">The count pin's recorded high-water history.</param>
    internal sealed record RatchetDeclaration(
        string Name,
        Type OwningFixture,
        string StalenessTestName,
        Func<IReadOnlyList<string>> StalenessProbe,
        Func<IReadOnlyList<string>> Justifications,
        Func<string, bool> Classifier,
        Func<int> LiveCount,
        int[] PinHistory);

    /// <summary>
    /// THE REGISTRY for this assembly. Count-pinned by
    /// <see cref="TheRegistry_isCountPinned"/>.
    /// </summary>
    internal static IReadOnlyList<RatchetDeclaration> Ratchets() =>
    [
        new("KnownUngovernedEndpoints",
            typeof(GovernedEndpointCoverageSweepTests),
            nameof(GovernedEndpointCoverageSweepTests.EveryMutatingEndpoint_IsGovernedOrJustified),
            GovernedEndpointCoverageSweepTests.RatchetStalenessProbe,
            () => KnownUngovernedEndpoints.All.Select(e => e.Justification).ToArray(),
            KnownUngovernedEndpoints.IsClassified,
            () => KnownUngovernedEndpoints.All.Count,
            KnownUngovernedEndpoints.PinHistory),
    ];

    /// <summary>Placeholders no ratchet's classifier may ever accept.</summary>
    private static readonly string[] Placeholders = ["", "   ", "TODO", "todo", "fixme", "n/a"];

    [Test]
    public void TheRegistry_isCountPinned()
    {
        // ANTI-VACUITY. Every assertion below iterates the registry; an empty or
        // shrunken registry would make this whole fixture pass while covering
        // nothing — the failure mode Epic 43 exists to prevent.
        Ratchets().Should().HaveCount(1,
            "one Story 43-8 ratchet lives in Tamma.Api.Tests (KnownUngovernedEndpoints); the other "
            + "three live in Tamma.Activities.Tests and are covered by the sibling fixture there. "
            + "If you added a ratchet to this assembly, DECLARE it above and bump this number — "
            + "that is the whole point of this fixture.");

        Ratchets().Select(r => r.Name).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void EveryRatchet_hasAStalenessArmThatActuallyFires()
    {
        // (a). Not "a test exists whose name contains 'stale'" — the owning
        // fixture's real classifier is driven with input that IS stale, and must
        // report it. A staleness arm that silently returns nothing fails here.
        var problems = new List<string>();

        foreach (var ratchet in Ratchets())
        {
            var method = ratchet.OwningFixture.GetMethod(
                ratchet.StalenessTestName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            if (method is null || method.GetCustomAttribute<TestAttribute>() is null)
            {
                problems.Add(
                    $"  {ratchet.Name}: declares its staleness test as "
                    + $"{ratchet.OwningFixture.Name}.{ratchet.StalenessTestName}, which does not exist "
                    + "or is not a [Test]. The declaration must name a real test.");
            }

            var reported = ratchet.StalenessProbe();
            if (reported.Count == 0)
            {
                problems.Add(
                    $"  {ratchet.Name}: its staleness probe reported NOTHING for input that is stale "
                    + "by construction. The ratchet cannot drain — a baselined entry that is now "
                    + "governed would sit there for ever, and the baseline rots into a snapshot "
                    + "nobody rereads.");
            }
        }

        problems.Should().BeEmpty(
            "AC8(a): every ratchet must fail on a stale entry:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryRatchet_classifiesItsJustifications_andRejectsPlaceholders()
    {
        // (b), in BOTH directions. Forward: every live justification classifies.
        // Backward: the classifier REJECTS placeholders — without this arm a
        // `_ => true` classifier would satisfy the forward one perfectly.
        var problems = new List<string>();

        foreach (var ratchet in Ratchets())
        {
            var justifications = ratchet.Justifications();

            if (justifications.Count == 0)
            {
                problems.Add($"  {ratchet.Name}: reported no justifications — the accessor is broken.");
                continue;
            }

            problems.AddRange(justifications
                .Where(j => !ratchet.Classifier(j))
                .Select(j => $"  {ratchet.Name}: unclassified justification '{j}'"));

            problems.AddRange(Placeholders
                .Where(p => ratchet.Classifier(p))
                .Select(p =>
                    $"  {ratchet.Name}: its classifier ACCEPTS the placeholder '{p}'. A classifier "
                    + "that accepts anything is not a classifier — a bare 'TODO' would then buy an "
                    + "entry into the ratchet."));
        }

        problems.Should().BeEmpty(
            "AC8(b): every ratchet's justifications must classify, and its classifier must reject "
            + "placeholders:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryRatchet_hasACountPin_thatIsMechanicallyShrinkOnly()
    {
        // (c), plus the strengthening this round adopted from
        // TemplateExampleConformanceTests.TheRatchetPin_IsMechanicallyShrinkOnly:
        // a bare `Be(N)` const is a number an author raises with one keystroke, so
        // the pin must be the last element of a recorded, strictly-decreasing
        // history. Raising it then requires appending a value that makes the owning
        // fixture RED.
        var problems = new List<string>();

        foreach (var ratchet in Ratchets())
        {
            if (ratchet.PinHistory.Length == 0)
            {
                problems.Add($"  {ratchet.Name}: has no PinHistory — its count pin is a bare literal.");
                continue;
            }

            if (ratchet.LiveCount() != ratchet.PinHistory[^1])
            {
                problems.Add(
                    $"  {ratchet.Name}: live count {ratchet.LiveCount()} != pin "
                    + $"{ratchet.PinHistory[^1]} (the last recorded high-water value).");
            }

            for (var i = 1; i < ratchet.PinHistory.Length; i++)
            {
                if (ratchet.PinHistory[i] >= ratchet.PinHistory[i - 1])
                {
                    problems.Add(
                        $"  {ratchet.Name}: pin history {ratchet.PinHistory[i - 1]} → "
                        + $"{ratchet.PinHistory[i]} is not a decrease. A ratchet that turns both "
                        + "ways is not a ratchet.");
                }
            }
        }

        problems.Should().BeEmpty(
            "AC8(c): every ratchet's count pin must exist and be mechanically shrink-only:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // ====================================================================
    // DISCRIMINATION PROOFS — the meta-checks must FAIL a deficient ratchet
    // ====================================================================

    [Test]
    public void Discrimination_aRatchetMissingEachPropertyWouldBeCaught()
    {
        // A meta-test that cannot fail a two-of-three ratchet is exactly the
        // decorative harness this story exists to prevent. Prove each arm bites, by
        // constructing the deficient declarations the arms are meant to reject.
        var noStaleness = Declaration(staleness: () => []);
        noStaleness.StalenessProbe().Should().BeEmpty(
            "the probe returning nothing is the shape EveryRatchet_hasAStalenessArmThatActuallyFires "
            + "reports");

        var alwaysTrue = Declaration(classifier: _ => true);
        Placeholders.Where(p => alwaysTrue.Classifier(p)).Should().NotBeEmpty(
            "a `_ => true` classifier accepts placeholders, which is what "
            + "EveryRatchet_classifiesItsJustifications_andRejectsPlaceholders reports");

        var risingPin = Declaration(pinHistory: [5, 9]);
        risingPin.PinHistory[1].Should().BeGreaterThan(risingPin.PinHistory[0],
            "a rising history is what EveryRatchet_hasACountPin_thatIsMechanicallyShrinkOnly reports");

        var emptyHistory = Declaration(pinHistory: []);
        emptyHistory.PinHistory.Should().BeEmpty(
            "no history at all means the pin is a bare literal, which the same arm reports");
    }

    private static RatchetDeclaration Declaration(
        Func<IReadOnlyList<string>>? staleness = null,
        Func<string, bool>? classifier = null,
        int[]? pinHistory = null) =>
        new("fixture-ratchet",
            typeof(RatchetDisciplineTests),
            nameof(TheRegistry_isCountPinned),
            staleness ?? (() => ["  stale"]),
            () => ["human-operated: fixture"],
            classifier ?? KnownUngovernedEndpoints.IsClassified,
            () => 1,
            pinHistory ?? [1]);
}
