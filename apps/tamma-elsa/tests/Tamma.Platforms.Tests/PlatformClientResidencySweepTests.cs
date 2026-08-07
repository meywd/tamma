using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Epic 31 P1 (stage 1) — THE INVARIANT RATCHET (execution plan §2): <i>no
/// production code path may reference a platform-specific client type outside
/// that platform's driver project.</i> Made mechanical in the
/// <c>ActionGovernanceResidencyTests</c> / <c>CallerKindResidencyTests</c>
/// source-scan shape (reading source in a test is justified where the
/// invariant has no reflectable surface) with the
/// <c>KnownUngovernedEndpoints</c> ratchet discipline: a pinned baseline that
/// enumerates today's violations exactly, a count pin whose history may only
/// shrink, and staleness both ways.
///
/// <para><b>Scope.</b> The scan covers <c>src/</c> of the four production
/// projects (Tamma.Api, Tamma.Activities, Tamma.ElsaServer, Tamma.Core) —
/// not tests, and not the <c>Tamma.Platforms.*</c> driver projects, which are
/// exactly where platform-specific types are SUPPOSED to live. Comment-only
/// lines are ignored so prose about a type ("no IGitHubIntegrationService
/// here") doesn't count as a reference.</para>
///
/// <para><b>How the baseline drains.</b> P2 swaps GitMediationService /
/// GitTokenResolver onto the driver plane (seams 1-2, 7, 14); P3 reroutes
/// CI, agent dispatch and the engine callbacks and deletes the then-empty
/// delegator classes (seams 3-6); P4 takes webhooks + CI secrets (seams
/// 8-11). Each phase DELETES entries here and appends the shrunk count to
/// <see cref="PinHistory"/>. Seams 12 (latent DI registrations) and 13 (the
/// three orphaned engine activities) were removed outright in this stage, so
/// they never enter the baseline.</para>
/// </summary>
[TestFixture]
public class PlatformClientResidencySweepTests
{
    // ====================================================================
    // Scan machinery (the CallerKindResidencyTests repo-root shape).
    // ====================================================================

    private static string RepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tamma.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the sweep must locate the repo root to read source files");
        return dir!;
    }

    /// <summary>The four production projects the invariant governs.</summary>
    private static readonly string[] ScannedProjects =
    [
        "Tamma.Api",
        "Tamma.Activities",
        "Tamma.ElsaServer",
        "Tamma.Core",
    ];

    /// <summary>
    /// The platform-specific client tokens (plan §2's type list, plus the
    /// factory chokepoint and the unregistered named-HttpClient bypass the
    /// same types ride on). Substring match on non-comment lines, ordinal —
    /// "GitHubIntegrationService" also catches the I-prefixed interface, and
    /// "Octokit" catches both the package namespace and the Octokit* client
    /// class names.
    /// </summary>
    private static readonly string[] PlatformClientTokens =
    [
        "GitHubIntegrationService",
        "CIIntegrationService",
        "IGitHubActionsClient",
        "IGitHubEngineCallbackService",
        "IGitHubAppClient",
        "IGitHubClientFactory",
        "Octokit",
        "GiteaHttpClient",
        "GitLabHttpClient",
        "CreateClient(\"github\")",
    ];

    private static IEnumerable<string> SourceFiles() =>
        ScannedProjects
            .SelectMany(p => Directory.GetFiles(
                Path.Combine(RepoRoot(), "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Rel(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace('\\', '/');

    /// <summary>
    /// Tokens referenced by a file on non-comment lines; empty when clean.
    /// </summary>
    private static string[] TokensReferencedBy(string file)
    {
        var codeLines = File.ReadLines(file)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToArray();
        return PlatformClientTokens
            .Where(t => codeLines.Any(l => l.Contains(t, StringComparison.Ordinal)))
            .ToArray();
    }

    // ====================================================================
    // The baseline — today's violations, EXACTLY, each carrying the seam it
    // belongs to (plan §1's seam table) and the phase that deletes it.
    // Ordinal-sorted by path; asserted below.
    // ====================================================================

    /// <summary>One baselined file.</summary>
    /// <param name="Path">Repo-root-relative path, '/' separators.</param>
    /// <param name="Reason">Which seam this file is, and which phase (P2/P3/P4) deletes the entry.</param>
    internal sealed record Entry(string Path, string Reason);

    internal static readonly IReadOnlyList<Entry> Baseline =
    [
        // P2 (2026-08-07) DELETED five entries — the ratchet's first turn after
        // seeding: the three ADL ExecuteCoreAsync helpers (retyped onto
        // IGitPlatformClient), GitMediationService.cs (17 op cores swapped onto
        // IPlatformResolver → driver.Client), and IGitHubClientFactory.cs (the
        // chokepoint, deleted outright). Pin 26 → 21.
        new("src/Tamma.Activities/AgentDispatch/IGitHubActionsClient.cs",
            "seam 6: the IGitHubActionsClient seam definition; P3 swaps consumers onto driver.Actions and deletes the surface"),
        new("src/Tamma.Activities/AgentDispatch/NullGitHubActionsClient.cs",
            "seam 6: null implementation of the IGitHubActionsClient seam; deleted with the seam in P3"),
        new("src/Tamma.Api/Endpoints/EngineEndpoints.cs",
            "seam 5: the 8 engine-callback handlers ride IGitHubEngineCallbackService; P3 reroutes them onto the driver plane"),
        new("src/Tamma.Api/Extensions/GitHubInstallationServiceCollectionExtensions.cs",
            "seam 7/10: DI wiring for the Octokit App client + installation router; P3/P4 move it into the GitHub driver"),
        new("src/Tamma.Api/Program.cs",
            "seams 3/5/6/7: composition root still registers IGitHubActionsClient/Octokit clients and the engine-callback service; drains across P2-P4"),
        new("src/Tamma.Api/Services/AgentDispatch/ActionsResultAggregator.cs",
            "seam 6: aggregates workflow-run results via IGitHubActionsClient; P3 swaps it onto driver.Actions"),
        new("src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs",
            "seam 6: dispatches agent runs via IGitHubActionsClient (IGitRepoAuthorizer-guarded); P3 swaps it onto driver.Actions"),
        new("src/Tamma.Api/Services/CIIntegrationService.cs",
            "seam 3: the static-token CI client over the named \"github\" HttpClient; P1 stage 2 absorbs the body, P3 deletes the class"),
        new("src/Tamma.Api/Services/Ci/CiClientFactory.cs",
            "seam 3: mints token-bound CIIntegrationService per request; P3 backs /api/v1/ci/* with driver.Actions and deletes it"),
        new("src/Tamma.Api/Services/Engine/IGitHubEngineCallbackService.cs",
            "seam 5: the engine-callback client seam definition; P3 deletes it"),
        new("src/Tamma.Api/Services/Engine/NullGitHubEngineCallbackService.cs",
            "seam 5: null implementation of the engine-callback seam; deleted with the seam in P3"),
        new("src/Tamma.Api/Services/Engine/OctokitGitHubEngineCallbackService.cs",
            "seam 5: Octokit implementation of the engine-callback seam; P3 moves the bodies into the GitHub driver"),
        new("src/Tamma.Api/Services/GitHub/IGitHubAppClient.cs",
            "seam 7: the App-level Octokit client seam definition; P2/P3 move it inside the GitHub driver"),
        new("src/Tamma.Api/Services/GitHub/InstallationRouterService.cs",
            "seam 10: install-time provisioning via Octokit + the [Obsolete] provisioner; P4 migrates it"),
        new("src/Tamma.Api/Services/GitHub/LibsodiumGitHubSecretsProvisioner.cs",
            "seam 10/11: libsodium CI-secrets provisioning over Octokit; P4 mounts it on the GitHub driver factory"),
        new("src/Tamma.Api/Services/GitHub/NullGitHubAppClient.cs",
            "seam 7: null implementation of the App-client seam; deleted with the seam in P2/P3"),
        new("src/Tamma.Api/Services/GitHub/OctokitGitHubActionsClient.cs",
            "seam 6/7: Octokit implementation of IGitHubActionsClient (App-token conditional); P1 stage 2/P3 absorb it into the driver"),
        new("src/Tamma.Api/Services/GitHub/OctokitGitHubAppClient.cs",
            "seam 7: Octokit App client, default github.com base; P2/P3 move it into the GitHub driver"),
        new("src/Tamma.Api/Services/GitHubIntegrationService.cs",
            "seam 1: the 1074-line live GitHub REST/GraphQL client; P1 stage 2 absorbs the body into Tamma.Platforms.GitHub, P3 deletes the delegator"),
        new("src/Tamma.Api/Services/IntegrationService.cs",
            "seam 12 residue: the legacy composite facade over IGitHubIntegrationService/ICIIntegrationService — DI registration removed in P1 stage 1, class deleted when P3 removes the interfaces"),
        new("src/Tamma.Core/Interfaces/IIntegrationService.cs",
            "seams 1/3: defines IGitHubIntegrationService + ICIIntegrationService and their DTOs; P3 deletes them with the delegators"),
    ];

    /// <summary>
    /// The count pin. SEEDED 2026-08-07 (Epic 31 P1 stage 1) from the sweep
    /// itself: 26 files. Stage 1 already turned the ratchet before seeding —
    /// the three orphaned engine activities (seam 13: ContextGatheringActivity,
    /// FetchFileContentsActivity, FetchSimilarPatternsActivity — direct GitHub
    /// REST through the unregistered "github" named client) were DELETED, and
    /// the latent DI registrations (seam 12, Program.cs:313-317) removed, so
    /// neither appears in the baseline at all. May only go DOWN; every
    /// decrement ships with the deleted entries in the same diff.
    /// </summary>
    internal const int PinnedCount = 21;

    /// <summary>
    /// The pin's recorded history, oldest first; every element after the seed
    /// must be strictly LESS than its predecessor (the
    /// <c>KnownUngovernedEndpoints.PinHistory</c> shape — moves shrink-only
    /// from prose into a diffable literal). Raising the pin requires
    /// appending a value that makes this fixture RED.
    /// </summary>
    /// <para><b>26 → 21 (2026-08-07, Epic 31 P2).</b> The mediation swap:
    /// GitMediationService.cs + IGitHubClientFactory.cs (deleted) + the three
    /// ADL ExecuteCoreAsync helpers (retyped onto IGitPlatformClient) left the
    /// baseline in the same diff.</para>
    internal static readonly int[] PinHistory = [26, 21];

    // ====================================================================
    // The sweep against reality.
    // ====================================================================

    [Test]
    public void TheScan_ActuallySeesTheProductionTree()
    {
        // Anti-no-op tripwire: if repo-root discovery or the project globs
        // break, every assertion below passes vacuously on an empty list.
        // The four projects hold ~1,278 source files today.
        SourceFiles().Count().Should().BeGreaterThan(800,
            "a tiny scan means the source discovery broke, not that the tree shrank");
    }

    [Test]
    public void PlatformClientReferences_MatchThePinnedBaseline()
    {
        var found = SourceFiles()
            .Select(f => (Path: Rel(f), Tokens: TokensReferencedBy(f)))
            .Where(f => f.Tokens.Length > 0)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToArray();

        var baselinePaths = Baseline.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);

        // Direction 1 — a NEW violating file is a red build, with the fix
        // named. This is the invariant doing its job: route the call through
        // the resolved driver's IGitPlatformClient / IGitPlatformActionsClient
        // instead of a platform-typed client.
        var newViolations = found
            .Where(f => !baselinePaths.Contains(f.Path))
            .Select(f => $"  {f.Path}: references [{string.Join(", ", f.Tokens)}]")
            .ToList();
        newViolations.Should().BeEmpty(
            "no production code path may reference a platform-specific client type outside that "
            + "platform's driver project (Epic 31 plan §2). Route the operation through the "
            + "resolved IGitPlatformDriver instead. This baseline may only SHRINK — do not add "
            + "an entry:" + Environment.NewLine + string.Join(Environment.NewLine, newViolations));

        // Direction 2 — staleness: an entry whose file is clean (or gone) has
        // been FIXED; the baseline drains instead of rotting.
        var foundPaths = found.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var stale = Baseline
            .Where(e => !foundPaths.Contains(e.Path))
            .Select(e => $"  {e.Path}")
            .ToList();
        stale.Should().BeEmpty(
            "these baselined files no longer reference any platform-client token (or no longer "
            + "exist) — the ratchet turned! DELETE their entries and append the shrunk count to "
            + "PinHistory in the same diff:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Test]
    public void Baseline_IsOrdinallySortedAndDistinct()
    {
        var paths = Baseline.Select(e => e.Path).ToArray();
        paths.Should().BeInAscendingOrder(StringComparer.Ordinal,
            "a sorted baseline keeps diffs reviewable (the pinned-sweep convention)");
        paths.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Baseline_EveryEntryNamesItsSeamAndItsExit()
    {
        // A placeholder cannot buy an entry: each must tie back to the plan's
        // seam table AND name the phase (or stage) that deletes it.
        var unclassified = Baseline
            .Where(e => !e.Reason.Contains("seam", StringComparison.OrdinalIgnoreCase)
                     || !(e.Reason.Contains("P2", StringComparison.Ordinal)
                       || e.Reason.Contains("P3", StringComparison.Ordinal)
                       || e.Reason.Contains("P4", StringComparison.Ordinal)))
            .Select(e => $"  {e.Path}: {e.Reason}")
            .ToList();
        unclassified.Should().BeEmpty(
            "every baseline entry must name its seam (plan §1) and the phase that deletes it:"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified));
    }

    [Test]
    public void Baseline_CountIsPinned()
    {
        Baseline.Should().HaveCount(PinnedCount,
            "the platform-client baseline may only SHRINK. If this fails because you added an "
            + "entry, that is the ratchet working: the new reference should go through the "
            + "driver plane instead");
        PinnedCount.Should().Be(PinHistory[^1],
            "the pin is the last element of its recorded history — change both together");
    }

    [Test]
    public void TheRatchetPin_IsMechanicallyShrinkOnly()
    {
        PinHistory.Should().NotBeEmpty();
        PinHistory[0].Should().Be(26,
            "seeded 2026-08-07 from the stage-1 sweep (after the seam-12/13 deletions)");
        for (var i = 1; i < PinHistory.Length; i++)
        {
            PinHistory[i].Should().BeLessThan(PinHistory[i - 1],
                $"pin history entry #{i} ({PinHistory[i]}) must be strictly smaller than "
                + $"#{i - 1} ({PinHistory[i - 1]}): a file leaves this baseline by moving onto "
                + "the driver plane, never by the baseline growing to fit it");
        }
    }

    [Test]
    public void TheOrphanedEngineActivities_StayDeleted()
    {
        // Seam 13's exit was DELETION (the three activities made direct
        // GitHub REST calls through an unregistered named client — the calls
        // could only throw). Pin the absence so they cannot quietly return.
        string[] deleted =
        [
            "src/Tamma.Activities/AI/ContextGatheringActivity.cs",
            "src/Tamma.Activities/Context/FetchFileContentsActivity.cs",
            "src/Tamma.Activities/Context/FetchSimilarPatternsActivity.cs",
        ];
        foreach (var path in deleted)
        {
            File.Exists(Path.Combine(RepoRoot(), path)).Should().BeFalse(
                $"{path} was deleted by Epic 31 P1 stage 1 (orphaned, direct GitHub REST via an "
                + "unregistered named HttpClient); a platform call belongs behind IGitPlatformClient");
        }
    }
}
