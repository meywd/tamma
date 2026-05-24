using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Story 27-16 (AC2 + AC5) — the anti-drift guarantee.
///
/// <para>This is the CORE deliverable for a repo with NO generated files: the
/// prompt seed (in-code <see cref="SystemPrompts.RoleActionTemplates"/>), the
/// convention seed (<see cref="ConventionSeedSpecs.Build"/>), and the
/// authoritative taxonomy (<see cref="RolePhaseMap.EligibleActions"/>) all key
/// off the SAME frozen <c>(role, action)</c> grid. If any of the three keysets
/// gains or loses a pair relative to the others, this test FAILS with a clear
/// diff. It runs in the existing <c>dotnet-tests</c> CI job (which runs
/// <c>dotnet test</c>), so drift = a red CI build — the "generator runs in CI,
/// tree clean" guarantee for a no-codegen repo.</para>
///
/// <para>All three keysets are derived WITHOUT a database: the convention seed
/// keyset comes from the pure static <see cref="ConventionSeedSpecs.Build"/>,
/// not from <see cref="ConventionStoreSeeder"/> (which needs a DB).</para>
/// </summary>
[TestFixture]
public class ConventionSeedDriftTests
{
    private static HashSet<(string Role, string Action)> PromptKeyset() =>
        SystemPrompts.RoleActionTemplates
            .Select(t => (Role: t.Role!, t.Action))
            .ToHashSet();

    private static HashSet<(string Role, string Action)> ConventionSeedKeyset() =>
        ConventionSeedSpecs.Build()
            .Select(s => (s.Role, s.Action))
            .ToHashSet();

    private static HashSet<(string Role, string Action)> TaxonomyKeyset() =>
        RolePhaseMap.EligibleActions
            .SelectMany(kv => kv.Value.Select(a => (Role: kv.Key.ToWire(), Action: a.ToWire())))
            .ToHashSet();

    [Test]
    public void ConventionSeedKeyset_EqualsTaxonomyKeyset()
    {
        var convention = ConventionSeedKeyset();
        var taxonomy = TaxonomyKeyset();

        convention.Should().BeEquivalentTo(taxonomy,
            "the convention seed must cover EXACTLY the RolePhaseMap (role, action) cells — " +
            "no missing cells, no extras. " + DiffMessage(convention, taxonomy));
    }

    [Test]
    public void PromptKeyset_EqualsTaxonomyKeyset()
    {
        var prompt = PromptKeyset();
        var taxonomy = TaxonomyKeyset();

        prompt.Should().BeEquivalentTo(taxonomy,
            "the prompt registry must cover EXACTLY the RolePhaseMap (role, action) cells. " +
            DiffMessage(prompt, taxonomy));
    }

    [Test]
    public void PromptKeyset_EqualsConventionSeedKeyset()
    {
        var prompt = PromptKeyset();
        var convention = ConventionSeedKeyset();

        // The transitive equality (both == taxonomy) already implies this, but
        // asserting it directly produces the clearest failure message when the
        // two seeds drift apart from each other.
        prompt.Should().BeEquivalentTo(convention,
            "the prompt seed and the convention seed must share an IDENTICAL " +
            "(role, action) keyset — they cannot drift because both derive from " +
            "RolePhaseMap.EligibleActions. " + DiffMessage(prompt, convention));
    }

    [Test]
    public void AllThreeKeysets_AreIdentical()
    {
        var prompt = PromptKeyset();
        var convention = ConventionSeedKeyset();
        var taxonomy = TaxonomyKeyset();

        prompt.Should().BeEquivalentTo(taxonomy);
        convention.Should().BeEquivalentTo(taxonomy);
        prompt.Count.Should().Be(convention.Count).And.Be(taxonomy.Count,
            "all three keysets must have the same cardinality");
    }

    /// <summary>Human-readable symmetric diff for failure messages.</summary>
    private static string DiffMessage(
        HashSet<(string Role, string Action)> a,
        HashSet<(string Role, string Action)> b)
    {
        var onlyInA = a.Except(b).OrderBy(x => x.Role).ThenBy(x => x.Action).ToList();
        var onlyInB = b.Except(a).OrderBy(x => x.Role).ThenBy(x => x.Action).ToList();
        if (onlyInA.Count == 0 && onlyInB.Count == 0)
        {
            return "(no diff)";
        }

        var left = onlyInA.Count == 0
            ? "none"
            : string.Join(", ", onlyInA.Select(x => $"{x.Role}/{x.Action}"));
        var right = onlyInB.Count == 0
            ? "none"
            : string.Join(", ", onlyInB.Select(x => $"{x.Role}/{x.Action}"));
        return $"Only in first: [{left}]. Only in second: [{right}].";
    }
}
