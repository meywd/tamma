using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea;
using Tamma.Platforms.GitHub;
using Tamma.Platforms.GitLab;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Epic 31 P1 — the capability CONTRACT test: for every shipped driver
/// kind, the driver-computed capability set must agree with verb
/// reality, in both directions, for every capability-gated verb family
/// on <see cref="IGitPlatformClient"/>:
///
/// <list type="number">
///   <item><b>Advertised ⇒ implemented.</b> A driver advertising a capability
///   must not answer that capability's verbs with
///   <c>capability_unsupported</c>, and must not answer them with the bare
///   <see cref="PlatformResult{T}.ServiceUnavailable"/> stub either — per the
///   <see cref="PlatformResult{T}"/> doc that variant means "driver isn't
///   wired", which is exactly the lie this test exists to catch (a matrix flag
///   over a full stub).</item>
///   <item><b>Not advertised ⇒ typed refusal.</b> A driver NOT advertising a
///   capability must answer that capability's verbs with
///   <see cref="PlatformError.InvalidRequest"/> code
///   <c>capability_unsupported</c> — never a throw, never a silent
///   stub — and must do so without touching the network. Two probe rows
///   relax this arm (see <c>RelaxedWhenNotAdvertised</c>): PrFileReview and
///   ListAccessibleRepos predate the typed-refusal convention — their
///   interface contract lets an unwired driver answer the bare stub / an
///   empty sequence, so the relaxed arm only demands "no lie", not the
///   exact refusal code.</item>
/// </list>
///
/// <para><b>Stage 2 note (2026-08-07)</b>: the exemption list is now EMPTY —
/// P1 stage 2 made the GitHub driver real (all lifecycle + loop verbs + the
/// probe-bearing repo listing make HTTP), deleting the single pinned
/// GitHub/PrLifecycle lie and appending the terminal 0 to
/// <see cref="ExemptionPinHistory"/>. The ratchet discipline stays: any NEW
/// lie fails the main test, and the membership pin forbids the list ever
/// growing again.</para>
/// </summary>
[TestFixture]
public class PlatformCapabilityContractTests
{
    // ====================================================================
    // The capability → verb map. Only verb families whose capability the
    // matrix gates are listed. The pre-31-13 core verbs (GetRepo,
    // OpenPullRequest, …) answer ServiceUnavailable when a driver is
    // unwired and are outside this contract. P1 stage 2 added the
    // PrFileReview and ListAccessibleRepos rows once the GitHub driver
    // stopped stubbing both.
    // ====================================================================

    private sealed record VerbProbe(
        PlatformCapability Capability,
        string VerbName,
        Func<DriverCase, Task<VerbAnswer>> Invoke,
        bool RelaxedWhenNotAdvertised = false);

    /// <summary>How a verb answered, classified per the result contract.</summary>
    private sealed record VerbAnswer(bool CapabilityUnsupported, bool NotWiredStub);

    private static VerbAnswer Classify<T>(PlatformResult<T> result) => new(
        CapabilityUnsupported:
            result is PlatformResult<T>.Failed f
            && f.Error is PlatformError.InvalidRequest ir
            && string.Equals(ir.Code, "capability_unsupported", StringComparison.Ordinal),
        NotWiredStub: result is PlatformResult<T>.ServiceUnavailable);

    /// <summary>
    /// The accessible-repos listing has no <see cref="PlatformResult{T}"/>
    /// envelope. Classification: a REAL implementation either yields, or
    /// attempts HTTP (visible on the case's request counter — every case
    /// scripts a 500 server), or throws its typed failure. Completing
    /// silently-empty WITHOUT any HTTP is the old GitHub
    /// <c>yield break</c> stub — the vacuous-probe lie.
    /// </summary>
    private static async Task<VerbAnswer> ClassifyListAccessibleRepos(DriverCase driver)
    {
        var before = driver.RequestCount();
        var yielded = false;
        var threw = false;
        try
        {
            await foreach (var _ in driver.Client.ListAccessibleReposAsync())
            {
                yielded = true;
                break;
            }
        }
        catch
        {
            threw = true;
        }
        var attemptedHttp = driver.RequestCount() > before;
        return new VerbAnswer(
            CapabilityUnsupported: false,
            NotWiredStub: !yielded && !threw && !attemptedHttp);
    }

    private static readonly VerbProbe[] CapabilityVerbs =
    [
        // Story 31-13 — the six PR lifecycle verbs.
        new(PlatformCapability.PrLifecycle, "ClosePullRequestAsync",
            async d => Classify(await d.Client.ClosePullRequestAsync("o", "r", "1"))),
        new(PlatformCapability.PrLifecycle, "ReopenPullRequestAsync",
            async d => Classify(await d.Client.ReopenPullRequestAsync("o", "r", "1"))),
        new(PlatformCapability.PrLifecycle, "RequestReviewersAsync",
            async d => Classify(await d.Client.RequestReviewersAsync(
                new RequestReviewersRequest("o", "r", "1", ["alice"])))),
        new(PlatformCapability.PrLifecycle, "AddPullRequestLabelsAsync",
            async d => Classify(await d.Client.AddPullRequestLabelsAsync(
                new AddPullRequestLabelsRequest("o", "r", "1", ["bug"])))),
        new(PlatformCapability.PrLifecycle, "RemovePullRequestLabelAsync",
            async d => Classify(await d.Client.RemovePullRequestLabelAsync("o", "r", "1", "bug"))),
        new(PlatformCapability.PrLifecycle, "SetDraftAsync",
            async d => Classify(await d.Client.SetDraftAsync(
                new SetPullRequestDraftRequest("o", "r", "1", Draft: false)))),

        // Epic 31 P1 stage 1 — the loop verbs.
        new(PlatformCapability.IssueLifecycle, "CloseIssueAsync",
            async d => Classify(await d.Client.CloseIssueAsync("o", "r", "1"))),
        new(PlatformCapability.IssueLifecycle, "AddIssueLabelsAsync",
            async d => Classify(await d.Client.AddIssueLabelsAsync(
                new AddIssueLabelsRequest("o", "r", "1", ["bug"])))),
        new(PlatformCapability.IssueLifecycle, "RemoveIssueLabelAsync",
            async d => Classify(await d.Client.RemoveIssueLabelAsync("o", "r", "1", "bug"))),
        new(PlatformCapability.Releases, "CreateReleaseAsync",
            async d => Classify(await d.Client.CreateReleaseAsync(
                new CreateReleaseRequest("o", "r", "v1.0.0")))),
        new(PlatformCapability.PrReviewCommentRead, "ListPullRequestReviewCommentsAsync",
            async d => Classify(await d.Client.ListPullRequestReviewCommentsAsync("o", "r", "1"))),
        new(PlatformCapability.CommitReads, "ListCommitsAsync",
            async d => Classify(await d.Client.ListCommitsAsync(
                new ListCommitsRequest("o", "r", "main")))),
        new(PlatformCapability.CommitReads, "ListBranchFileChangesAsync",
            async d => Classify(await d.Client.ListBranchFileChangesAsync(
                new ListBranchFileChangesRequest("o", "r", "feature")))),

        // Epic 31 P1 stage 2 — the two capabilities GitHub used to stub
        // while the matrix advertised them. Relaxed not-advertised arm:
        // their interface contract predates the typed-refusal convention
        // (an unwired driver answers the bare stub / an empty sequence).
        new(PlatformCapability.PrFileReview, "CreatePullRequestReviewCommentAsync",
            async d => Classify(await d.Client.CreatePullRequestReviewCommentAsync(
                new CreatePullRequestReviewCommentRequest("o", "r", "1", "f.cs", 1, "b", "sha"))),
            RelaxedWhenNotAdvertised: true),
        new(PlatformCapability.ListAccessibleRepos, "ListAccessibleReposAsync",
            ClassifyListAccessibleRepos,
            RelaxedWhenNotAdvertised: true),
    ];

    // ====================================================================
    // Driver cases — one per shipped driver kind, plus the null seam.
    // Clients are built exactly the way the factories build them, minus the
    // startup version probe, over an HttpClient whose counting handler
    // answers 500 — a verb that is really implemented surfaces a
    // Failed(...) error envelope from that 500 (never capability_unsupported
    // and never the bare not-wired ServiceUnavailable result), so the
    // classification stays honest even if a stub grows HTTP by accident.
    // ====================================================================

    public sealed record DriverCase(
        string Name,
        IReadOnlySet<PlatformCapability> Capabilities,
        IGitPlatformClient Client,
        Func<int> RequestCount)
    {
        public override string ToString() => Name;
    }

    private sealed class CountingAlways500Handler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private static DriverCase GitHubCase()
    {
        var handler = new CountingAlways500Handler();
        var http = new GitHubHttpClient(
            new HttpClient(handler),
            "https://api.github.com",
            new GitHubAuth.Pat("test-token"));
        return new DriverCase(
            "GitHub",
            // PAT-mode compute — the fullest source-host set (only the
            // App-auth flag is narrowed away, which no probe gates).
            GitHubPlatformDriver.ComputeCapabilities(new GitHubAuth.Pat("test-token")),
            new GitHubPlatformClient(http, "github.com"),
            () => handler.Count);
    }

    private static DriverCase GiteaStyleCase(string name, IReadOnlySet<PlatformCapability> capabilities)
    {
        var handler = new CountingAlways500Handler();
        var http = new GiteaHttpClient(
            new HttpClient(handler),
            Guid.NewGuid(),
            "https://gitea.example",
            new GiteaAuth.BotToken("test-token"),
            new GiteaOAuth2TokenCache());
        return new DriverCase(
            name,
            capabilities,
            new GiteaPlatformClient(http, "gitea.example"),
            () => handler.Count);
    }

    private static IEnumerable<DriverCase> DriverCases()
    {
        yield return GitHubCase();

        // Version 1.22 ≥ MinimumActionsVersion so nothing is narrowed away —
        // the fullest capability set the driver can compute.
        yield return GiteaStyleCase(
            "Gitea", GiteaPlatformDriver.ComputeCapabilities(new Version(1, 22)));

        yield return GiteaStyleCase(
            "Forgejo", ForgejoPlatformDriver.ComputeCapabilities(new Version(1, 22)));

        var gitlabHandler = new CountingAlways500Handler();
        yield return new DriverCase(
            "GitLab",
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitLab),
            new GitLabPlatformClient(
                new GitLabHttpClient(
                    new HttpClient(gitlabHandler),
                    new GitLabAuth.PersonalAccessToken("test-token"),
                    "https://gitlab.example"),
                NullLogger<GitLabPlatformClient>.Instance),
            () => gitlabHandler.Count);

        yield return new DriverCase(
            "Null",
            new HashSet<PlatformCapability>(),
            NullGitPlatformDriver.Instance.Client,
            () => 0);
    }

    // ====================================================================
    // The exemption list — DRAINED in P1 stage 2 and pinned empty.
    // ====================================================================

    private sealed record CapabilityLie(string DriverCase, PlatformCapability Capability, string Reason);

    /// <summary>
    /// Today's known lies, exactly: NONE. The single seeded entry
    /// (GitHub / PrLifecycle — the matrix advertising the 31-13 lifecycle
    /// over a fully-stubbed driver) was deleted in P1 stage 2 when the
    /// GitHub driver absorbed the live REST/GraphQL bodies. A new lie is
    /// a defect to fix in the driver, never a new entry here.
    /// </summary>
    private static readonly CapabilityLie[] KnownCapabilityLies = [];

    /// <summary>
    /// The exemption pin's recorded high-water history, oldest first —
    /// strictly decreasing after the seed, the sweep-ratchet shape. Seeded
    /// at 1 (2026-08-07, Epic 31 P1 stage 1: the GitHub PrLifecycle lie);
    /// reached its terminal 0 in P1 stage 2 (GitHub driver made real). It
    /// can never grow again.
    /// </summary>
    private static readonly int[] ExemptionPinHistory = [1, 0];

    /// <summary>
    /// Membership pin — permanently empty. A new (driver, capability)
    /// pair must be fixed in the driver, not exempted.
    /// </summary>
    private static readonly (string DriverCase, PlatformCapability Capability)[] AllowedExemptions = [];

    private static bool IsExempt(string driverCase, PlatformCapability capability) =>
        KnownCapabilityLies.Any(l =>
            string.Equals(l.DriverCase, driverCase, StringComparison.Ordinal)
            && l.Capability == capability);

    // ====================================================================
    // The contract, both directions.
    // ====================================================================

    [Test]
    [TestCaseSource(nameof(DriverCases))]
    public async Task AdvertisedCapabilities_MatchVerbReality(DriverCase driver)
    {
        var problems = new List<string>();

        foreach (var probe in CapabilityVerbs)
        {
            var advertised = driver.Capabilities.Contains(probe.Capability);
            var answer = await probe.Invoke(driver);

            if (advertised && !IsExempt(driver.Name, probe.Capability))
            {
                if (answer.CapabilityUnsupported)
                {
                    problems.Add(
                        $"  {driver.Name}.{probe.VerbName}: advertises {probe.Capability} but answers "
                        + "capability_unsupported — either implement the verb or stop advertising the flag.");
                }
                if (answer.NotWiredStub)
                {
                    problems.Add(
                        $"  {driver.Name}.{probe.VerbName}: advertises {probe.Capability} but answers the "
                        + "bare ServiceUnavailable stub (\"driver isn't wired\") — a capability flag over "
                        + "a stub is the GitHub lie this test pinned (fixed in P1 stage 2); do not add an "
                        + "exemption, fix the driver.");
                }
            }
            else if (!advertised && !probe.RelaxedWhenNotAdvertised)
            {
                if (!answer.CapabilityUnsupported)
                {
                    problems.Add(
                        $"  {driver.Name}.{probe.VerbName}: does NOT advertise {probe.Capability} yet does "
                        + "not answer the typed capability_unsupported failure the interface contract "
                        + "requires (no-throw, PlatformError.InvalidRequest, exact code).");
                }
            }
        }

        problems.Should().BeEmpty(
            "a driver's capability set and its verb reality must agree in both directions "
            + "(§2 of the Epic 31 execution plan — this is what makes CheckPlatformCapabilityActivity "
            + "trustworthy):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // ====================================================================
    // Ratchet discipline on the exemption list.
    // ====================================================================

    [Test]
    public void ExemptionList_MembershipIsPinned()
    {
        // Growth is a RED build by construction: a new lie fails
        // AdvertisedCapabilities_MatchVerbReality, and a new exemption entry
        // fails here because it is outside the (empty) pinned membership.
        foreach (var lie in KnownCapabilityLies)
        {
            AllowedExemptions.Should().Contain(
                (lie.DriverCase, lie.Capability),
                $"the capability-lie exemption list is pinned by (driver, capability); "
                + $"'{lie.DriverCase}/{lie.Capability}' is not a reviewed entry — fix the driver "
                + "instead of exempting it");
        }

        KnownCapabilityLies.Should().HaveCount(
            ExemptionPinHistory[^1],
            "the exemption count is pinned; P1 stage 2 took it to its terminal zero");
    }

    [Test]
    public void ExemptionPin_IsMechanicallyShrinkOnly()
    {
        ExemptionPinHistory.Should().NotBeEmpty();
        ExemptionPinHistory[0].Should().Be(1, "seeded at exactly the GitHub PrLifecycle lie");
        for (var i = 1; i < ExemptionPinHistory.Length; i++)
        {
            ExemptionPinHistory[i].Should().BeLessThan(ExemptionPinHistory[i - 1],
                $"pin history entry #{i} must be strictly smaller than #{i - 1}: an exemption "
                + "leaves this list by the driver becoming real, never by the list growing");
        }
        ExemptionPinHistory[^1].Should().Be(0,
            "P1 stage 2 drained the list; the terminal 0 is permanent");
    }

    [Test]
    public async Task Exemptions_AreStillRealLies_NotStaleEntries()
    {
        // The staleness arm — kept even though the list is empty so any
        // future (forbidden) exemption addition immediately re-arms it.
        var cases = DriverCases().ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var lie in KnownCapabilityLies)
        {
            cases.Should().ContainKey(lie.DriverCase,
                "an exemption must name a driver case this fixture actually builds");
            var driver = cases[lie.DriverCase];

            driver.Capabilities.Should().Contain(lie.Capability,
                $"'{lie.DriverCase}/{lie.Capability}' is exempted as an advertised-but-stubbed lie; "
                + "if the capability is no longer advertised the entry is stale — delete it");

            var verbs = CapabilityVerbs.Where(p => p.Capability == lie.Capability).ToArray();
            verbs.Should().NotBeEmpty("an exemption must gate a mapped verb family");

            var stillLying = false;
            foreach (var probe in verbs)
            {
                var answer = await probe.Invoke(driver);
                if (answer.CapabilityUnsupported || answer.NotWiredStub)
                {
                    stillLying = true;
                }
            }

            stillLying.Should().BeTrue(
                $"every verb of '{lie.DriverCase}/{lie.Capability}' now answers like a real "
                + "implementation — the lie is fixed, so DELETE this exemption entry and append "
                + "the shrunk count to ExemptionPinHistory");
        }
    }

    [Test]
    public void TheProbeTable_CoversEveryCapabilityGatedVerbFamily()
    {
        // Anti-no-op tripwire: the contract above is only as strong as the
        // probe table. Pin the families and the per-family verb counts so a
        // deleted probe row is a visible diff, not a silent coverage loss.
        CapabilityVerbs.Select(p => p.Capability).Distinct().Should().BeEquivalentTo(
        [
            PlatformCapability.PrLifecycle,
            PlatformCapability.IssueLifecycle,
            PlatformCapability.Releases,
            PlatformCapability.PrReviewCommentRead,
            PlatformCapability.CommitReads,
            PlatformCapability.PrFileReview,
            PlatformCapability.ListAccessibleRepos,
        ]);
        CapabilityVerbs.Should().HaveCount(15,
            "6 lifecycle + 3 issue-lifecycle + 1 release + 1 review-comment-read + 2 commit-read "
            + "+ 1 pr-file-review + 1 accessible-repos verbs; a new capability-gated verb must be "
            + "added here in the same commit");
    }
}
