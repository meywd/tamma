using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-13 AC1/D8 — "a grep proves no second site computes caller kind from
/// auth state", made mechanical (the <c>ActionGovernanceResidencyTests</c>
/// source-scan precedent: reading source in a test is justified where the
/// invariant has no reflectable surface).
///
/// <list type="number">
/// <item><b>Single computation site:</b> the auth-state inspection that
/// produces a <see cref="Tamma.Core.Actions.CallerKind"/> lives ONLY in
/// <c>CallerKindResolver.cs</c>. Two scans: no other Tamma.Api file both reads
/// <c>GetAuthPrincipal</c> and names <c>CallerKind</c>; and
/// <c>CallerKind.Human</c> — the value only an auth inspection may produce —
/// appears in no other source file at all.</item>
/// <item><b>The construction-site pin:</b> every <c>new AutonomyQuery(</c> in
/// <c>src/</c> is listed here. The query record is the only place a caller
/// kind can be declared, so a NEW site — which lands on the fail-closed Llm
/// default — must be classified by the author before this goes green.</item>
/// <item><b>Seam B is structurally Llm (D10):</b> the tool loop gate never
/// calls <c>Evaluate</c> and takes no <c>CallerKind</c> — pinned via its
/// doc-comment anchor rather than a constant plumbed through a sync interface
/// that could be mis-set.</item>
/// </list>
/// </summary>
[TestFixture]
public class CallerKindResidencyTests
{
    private static string RepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tamma.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must locate the repo root to read source files");
        return dir!;
    }

    private static IEnumerable<string> SourceFiles(string relativeRoot) =>
        Directory.GetFiles(
            Path.Combine(RepoRoot(), relativeRoot), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Rel(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace('\\', '/');

    [Test]
    public void TheAuthStateInspection_LivesOnlyInCallerKindResolver()
    {
        var offenders = SourceFiles(Path.Combine("src", "Tamma.Api"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("GetAuthPrincipal", StringComparison.Ordinal)
                    && text.Contains("CallerKind", StringComparison.Ordinal);
            })
            .Select(Rel)
            .ToArray();

        offenders.Should().Equal(
            new[] { "src/Tamma.Api/Services/Actions/CallerKindResolver.cs" },
            "exactly ONE function may turn auth state into a caller kind (AC1); a second "
            + "site is how the two drift apart");
    }

    [Test]
    public void CallerKindHuman_IsProducedNowhereElseInTheApi()
    {
        // `CallerKind.Human` is the value only the auth inspection may PRODUCE.
        // In Tamma.Api — the plane that holds auth state — the seams pass
        // resolver results, Seam D declares Machinery and defaults are Llm, so
        // a literal `CallerKind.Human` anywhere but the resolver is a second
        // computation site wearing a different spelling. (Tamma.Core CONSUMES
        // the value: the enum itself, the query doc, and the evaluator's
        // short-circuit comparison — none of them reads auth state.)
        var offenders = SourceFiles(Path.Combine("src", "Tamma.Api"))
            .Where(f => File.ReadAllText(f).Contains("CallerKind.Human", StringComparison.Ordinal))
            .Select(Rel)
            .ToArray();

        offenders.Should().Equal(
            new[] { "src/Tamma.Api/Services/Actions/CallerKindResolver.cs" });
    }

    [Test]
    public void TheAutonomyQueryConstructionSites_ArePinned()
    {
        // The five sites of record (43-13 D8) — the plan's count, verified
        // against the tree. A sixth site lands on the fail-closed Llm default,
        // which is SAFE but must be a reviewed classification, not an accident.
        var sites = SourceFiles("src")
            .Where(f => File.ReadAllText(f).Contains("new AutonomyQuery(", StringComparison.Ordinal))
            .Select(Rel)
            .ToArray();

        sites.Should().BeEquivalentTo(new[]
        {
            "src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs",     // Seam C — resolver
            "src/Tamma.Api/Services/Actions/BackgroundActionGate.cs",    // Seam D — Machinery
            "src/Tamma.Api/Endpoints/GovernanceEvaluateEndpoints.cs",    // Seam E — resolver
            "src/Tamma.Api/Endpoints/LlmCallEndpoints.cs",               // Seam A — resolver
            "src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs",          // policy view — default (LLM view)
        });
    }

    [Test]
    public void SeamB_IsStructurallyLlm_AndSaysSo()
    {
        // D10 — the tool loop gate's caller kind is the INPUT's provenance (a
        // model-emitted tool call), recorded in the interface doc. This anchor
        // fails if the doc is deleted or the interface grows a CallerKind
        // parameter without revisiting the design.
        var path = Path.Combine(
            RepoRoot(), "src", "Tamma.Api", "Services", "Agents", "IToolLoopAutonomyGate.cs");
        var text = File.ReadAllText(path);

        text.Should().Contain("Story 43-13 D10",
            "the structural-Llm fact must stay recorded on the seam it describes");
        text.Should().Contain("CallerKind.Llm");
        text.Should().NotContain("CallerKind caller",
            "a plumbed-through caller parameter is exactly the mis-settable constant "
            + "D10 rejected");
    }
}
