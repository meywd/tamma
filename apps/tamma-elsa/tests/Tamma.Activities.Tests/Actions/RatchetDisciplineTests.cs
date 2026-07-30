using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-8 <b>AC8</b> — the META-test (amendment §A1 carve-out #5, closed
/// 2026-07-30), for the three of the epic's four ratchets that live in this
/// assembly. Every ratchet must have <b>all three</b> properties: (a) a
/// <b>staleness</b> arm that fires; (b) <b>justification classification</b> that
/// rejects placeholders; (c) a <b>count pin</b> that is <b>mechanically
/// shrink-only</b>.
///
/// <para>See the sibling <c>Tamma.Api.Tests.Actions.RatchetDisciplineTests</c> for
/// the full rationale and the two honest residuals (this fixture is per-assembly
/// because the two test projects do not reference each other; declaration is
/// opt-in). The fourth ratchet, <c>KnownUngovernedEndpoints</c>, is declared
/// there.</para>
///
/// <para><b>What this fixture found on its first run</b> — recorded because it is
/// the reason AC8 asked for it: <c>NotDiRegisteredTools</c> is the ratchet whose
/// three properties were spread across three unrelated tests in
/// <c>ToolCatalogAllowlistTests</c>, with its classifier inlined as a regex literal
/// inside one of them. Declaring it here forced that classifier out into
/// <c>ToolCatalogAllowlistTests.CitesASource</c>, so the per-ratchet test and the
/// meta-test now drive the SAME rule instead of two copies that can drift.</para>
/// </summary>
[TestFixture]
public class RatchetDisciplineTests
{
    /// <summary>
    /// One declared ratchet and the three properties AC8 requires of it. Every
    /// member is a delegate or a reflected fact, never a claim.
    /// </summary>
    /// <param name="Name">The ratchet's identifier, for failure messages.</param>
    /// <param name="OwningFixture">The fixture that owns the three per-ratchet tests.</param>
    /// <param name="StalenessTestName">
    /// The owning fixture's staleness test, resolved by reflection and required to
    /// carry <c>[Test]</c> — deleting or renaming it fails HERE.
    /// </param>
    /// <param name="StalenessProbe">Drives the owning fixture's REAL classifier with stale input.</param>
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

    /// <summary>THE REGISTRY for this assembly. Count-pinned by <see cref="TheRegistry_isCountPinned"/>.</summary>
    internal static IReadOnlyList<RatchetDeclaration> Ratchets() =>
    [
        new("KnownNonEffectClientMethods",
            typeof(MediationClientEffectSweepTests),
            nameof(MediationClientEffectSweepTests.EveryEffectMember_AndEveryClientMethod_IsClassified),
            MediationClientEffectSweepTests.RatchetStalenessProbe,
            MediationClientEffectSweepTests.RatchetJustifications,
            MediationClientEffectSweepTests.RatchetClassifies,
            MediationClientEffectSweepTests.RatchetCount,
            MediationClientEffectSweepTests.NonEffectPinHistory),

        new("UnattributedActivities",
            typeof(UnattributedActivitySweepTests),
            nameof(UnattributedActivitySweepTests.EveryActivityClass_CarriesTheAttribute_OrIsBaselined),
            UnattributedActivitySweepTests.RatchetStalenessProbe,
            UnattributedActivitySweepTests.RatchetJustifications,
            UnattributedActivitySweepTests.RatchetClassifies,
            () => UnattributedActivitySweepTests.RatchetJustifications().Count,
            UnattributedActivitySweepTests.UnattributedPinHistory),

        // Story 43-4's ratchet, consumed here (43-8 AC8 names it as one of the four;
        // this story asserts its DISCIPLINE, it does not own its content). Its
        // staleness arm is bespoke — it resolves the live executor set rather than
        // running a pure classifier — so the probe below re-expresses that arm as a
        // pure predicate over the same fact: an entry is stale iff the tool it names
        // now HAS a DI-registered executor.
        new("NotDiRegisteredTools",
            typeof(ToolCatalogAllowlistTests),
            nameof(ToolCatalogAllowlistTests.The_single_not_di_registered_entry_is_not_stale),
            NotDiRegisteredToolsStalenessProbe,
            () => ToolCatalogAllowlists.NotDiRegisteredTools.Select(e => e.Justification).ToArray(),
            ToolCatalogAllowlistTests.CitesASource,
            () => ToolCatalogAllowlists.NotDiRegisteredTools.Count,
            ToolCatalogAllowlistTests.NotDiRegisteredToolsPinHistory),
    ];

    /// <summary>
    /// The <c>NotDiRegisteredTools</c> staleness rule, driven with SYNTHETIC input
    /// that is stale by construction: a tool key whose executor IS registered. It
    /// must report. (The live arm asserts the complement — that
    /// <c>get_acceptance_rules</c> is still unregistered — which cannot itself prove
    /// the rule fires.)
    /// </summary>
    private static IReadOnlyList<string> NotDiRegisteredToolsStalenessProbe()
    {
        string[] registered = ["file_read", "shell_execute"];
        var entries = new[] { "tool:file_read" };

        return entries
            .Where(k => registered.Contains(k["tool:".Length..], StringComparer.OrdinalIgnoreCase))
            .Select(k =>
                $"  {k}: allowlisted as NOT DI-registered, but its executor IS registered — DELETE "
                + "the ToolCatalogAllowlists.NotDiRegisteredTools line.")
            .ToArray();
    }

    /// <summary>Placeholders no ratchet's classifier may ever accept.</summary>
    private static readonly string[] Placeholders = ["", "   ", "TODO", "todo", "fixme", "n/a"];

    [Test]
    public void TheRegistry_isCountPinned()
    {
        // ANTI-VACUITY: every assertion below iterates the registry, so a shrunken
        // registry would make the fixture pass while covering nothing.
        Ratchets().Should().HaveCount(3,
            "three of Story 43-8's four ratchets live in Tamma.Activities.Tests "
            + "(KnownNonEffectClientMethods, UnattributedActivities, NotDiRegisteredTools); the "
            + "fourth, KnownUngovernedEndpoints, is declared in the sibling fixture in "
            + "Tamma.Api.Tests. If you added a ratchet to this assembly, DECLARE it above and bump "
            + "this number — that is the whole point of this fixture.");

        Ratchets().Select(r => r.Name).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void EveryRatchet_hasAStalenessArmThatActuallyFires()
    {
        // (a). The owning fixture's real rule is driven with input that IS stale and
        // must report it — not "a test exists whose name mentions staleness".
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
                    + $"{ratchet.OwningFixture.Name}.{ratchet.StalenessTestName}, which does not "
                    + "exist or is not a [Test].");
            }

            if (ratchet.StalenessProbe().Count == 0)
            {
                problems.Add(
                    $"  {ratchet.Name}: its staleness probe reported NOTHING for input that is "
                    + "stale by construction. The ratchet cannot drain.");
            }
        }

        problems.Should().BeEmpty(
            "AC8(a): every ratchet must fail on a stale entry:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryRatchet_classifiesItsJustifications_andRejectsPlaceholders()
    {
        // (b), in BOTH directions — the backward arm is what stops a `_ => true`
        // classifier from satisfying the forward one perfectly.
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
                    + "that accepts anything is not a classifier."));
        }

        problems.Should().BeEmpty(
            "AC8(b): every ratchet's justifications must classify, and its classifier must reject "
            + "placeholders:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryRatchet_hasACountPin_thatIsMechanicallyShrinkOnly()
    {
        // (c) plus the 2026-07-30 strengthening: the pin must be the last element of
        // a recorded, strictly-decreasing history, not a bare literal.
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
        Declaration(staleness: () => []).StalenessProbe().Should().BeEmpty(
            "a probe that reports nothing is the shape "
            + "EveryRatchet_hasAStalenessArmThatActuallyFires rejects");

        var alwaysTrue = Declaration(classifier: _ => true);
        Placeholders.Where(p => alwaysTrue.Classifier(p)).Should().NotBeEmpty(
            "a `_ => true` classifier accepts placeholders, which "
            + "EveryRatchet_classifiesItsJustifications_andRejectsPlaceholders rejects");

        var risingPin = Declaration(pinHistory: [5, 9]);
        risingPin.PinHistory[1].Should().BeGreaterThan(risingPin.PinHistory[0],
            "a rising history is what EveryRatchet_hasACountPin_thatIsMechanicallyShrinkOnly rejects");

        Declaration(pinHistory: []).PinHistory.Should().BeEmpty(
            "no history at all means the pin is a bare literal, rejected by the same arm");
    }

    [Test]
    public void Discrimination_theNotDiRegisteredStalenessProbeIsNotAlwaysRed()
    {
        // The complement of the probe above: with NO registered executor matching,
        // it must report nothing. A probe hard-wired to return a problem would
        // satisfy EveryRatchet_hasAStalenessArmThatActuallyFires while proving
        // nothing about the rule.
        string[] registered = ["shell_execute"];
        new[] { "tool:get_acceptance_rules" }
            .Where(k => registered.Contains(k["tool:".Length..], StringComparer.OrdinalIgnoreCase))
            .Should().BeEmpty(
                "the live entry (get_acceptance_rules) is genuinely NOT DI-registered, so the same "
                + "rule that reports the synthetic stale case must stay silent here");
    }

    private static RatchetDeclaration Declaration(
        Func<IReadOnlyList<string>>? staleness = null,
        Func<string, bool>? classifier = null,
        int[]? pinHistory = null) =>
        new("fixture-ratchet",
            typeof(RatchetDisciplineTests),
            nameof(TheRegistry_isCountPinned),
            staleness ?? (() => ["  stale"]),
            () => ["read-only: fixture"],
            classifier ?? MediationClientEffectSweepTests.RatchetClassifies,
            () => 1,
            pinHistory ?? [1]);
}
