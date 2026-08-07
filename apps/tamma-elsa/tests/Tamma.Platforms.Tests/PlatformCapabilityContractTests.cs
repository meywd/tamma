using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea;
using Tamma.Platforms.GitHub;
using Tamma.Platforms.GitLab;

namespace Tamma.Platforms.Tests;

/// <summary>
/// Epic 31 P1 (stage 1) — the capability CONTRACT test: for every shipped
/// driver kind, the driver-computed capability set must agree with verb
/// reality, in both directions, for every capability-gated verb family on
/// <see cref="IGitPlatformClient"/>:
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
///   stub — and must do so without touching the network.</item>
/// </list>
///
/// <para><b>The exemption list</b> (<see cref="KnownCapabilityLies"/>) is
/// pinned and SHRINK-ONLY, the governance-sweep shape: it enumerates today's
/// known lies exactly (one: GitHub's matrix advertises
/// <see cref="PlatformCapability.PrLifecycle"/> while
/// <see cref="GitHubPlatformClient"/> stubs all six lifecycle verbs). A NEW
/// lie fails the main test; GROWING the exemption list fails the membership
/// pin; a FIXED lie fails the staleness test until its entry is deleted.</para>
/// </summary>
[TestFixture]
public class PlatformCapabilityContractTests
{
    // ====================================================================
    // The capability → verb map. Only verb families whose interface
    // contract names capability_unsupported are listed; the pre-31-13 core
    // verbs (GetRepo, OpenPullRequest, …) answer ServiceUnavailable when a
    // driver is unwired and are outside this contract. PrFileReview and
    // ListAccessibleRepos are deliberately NOT mapped yet: GitHub's driver
    // stubs both while the matrix advertises them, and P1 stage 2 (which
    // makes the GitHub driver real) is the stage that can map them without
    // widening the exemption list beyond its pinned single entry.
    // ====================================================================

    private sealed record VerbProbe(
        PlatformCapability Capability,
        string VerbName,
        Func<IGitPlatformClient, Task<VerbAnswer>> Invoke);

    /// <summary>How a verb answered, classified per the result contract.</summary>
    private sealed record VerbAnswer(bool CapabilityUnsupported, bool NotWiredStub);

    private static VerbAnswer Classify<T>(PlatformResult<T> result) => new(
        CapabilityUnsupported:
            result is PlatformResult<T>.Failed f
            && f.Error is PlatformError.InvalidRequest ir
            && string.Equals(ir.Code, "capability_unsupported", StringComparison.Ordinal),
        NotWiredStub: result is PlatformResult<T>.ServiceUnavailable);

    private static readonly VerbProbe[] CapabilityVerbs =
    [
        // Story 31-13 — the six PR lifecycle verbs.
        new(PlatformCapability.PrLifecycle, "ClosePullRequestAsync",
            async c => Classify(await c.ClosePullRequestAsync("o", "r", "1"))),
        new(PlatformCapability.PrLifecycle, "ReopenPullRequestAsync",
            async c => Classify(await c.ReopenPullRequestAsync("o", "r", "1"))),
        new(PlatformCapability.PrLifecycle, "RequestReviewersAsync",
            async c => Classify(await c.RequestReviewersAsync(
                new RequestReviewersRequest("o", "r", "1", ["alice"])))),
        new(PlatformCapability.PrLifecycle, "AddPullRequestLabelsAsync",
            async c => Classify(await c.AddPullRequestLabelsAsync(
                new AddPullRequestLabelsRequest("o", "r", "1", ["bug"])))),
        new(PlatformCapability.PrLifecycle, "RemovePullRequestLabelAsync",
            async c => Classify(await c.RemovePullRequestLabelAsync("o", "r", "1", "bug"))),
        new(PlatformCapability.PrLifecycle, "SetDraftAsync",
            async c => Classify(await c.SetDraftAsync(
                new SetPullRequestDraftRequest("o", "r", "1", Draft: false)))),

        // Epic 31 P1 stage 1 — the loop verbs.
        new(PlatformCapability.IssueLifecycle, "CloseIssueAsync",
            async c => Classify(await c.CloseIssueAsync("o", "r", "1"))),
        new(PlatformCapability.IssueLifecycle, "AddIssueLabelsAsync",
            async c => Classify(await c.AddIssueLabelsAsync(
                new AddIssueLabelsRequest("o", "r", "1", ["bug"])))),
        new(PlatformCapability.IssueLifecycle, "RemoveIssueLabelAsync",
            async c => Classify(await c.RemoveIssueLabelAsync("o", "r", "1", "bug"))),
        new(PlatformCapability.Releases, "CreateReleaseAsync",
            async c => Classify(await c.CreateReleaseAsync(
                new CreateReleaseRequest("o", "r", "v1.0.0")))),
        new(PlatformCapability.PrReviewCommentRead, "ListPullRequestReviewCommentsAsync",
            async c => Classify(await c.ListPullRequestReviewCommentsAsync("o", "r", "1"))),
        new(PlatformCapability.CommitReads, "ListCommitsAsync",
            async c => Classify(await c.ListCommitsAsync(
                new ListCommitsRequest("o", "r", "main")))),
        new(PlatformCapability.CommitReads, "ListBranchFileChangesAsync",
            async c => Classify(await c.ListBranchFileChangesAsync(
                new ListBranchFileChangesRequest("o", "r", "feature")))),
    ];

    // ====================================================================
    // Driver cases — one per shipped driver kind, plus the null seam.
    // Clients are built exactly the way the factories build them, minus the
    // startup version probe, over an HttpClient whose handler answers 500 —
    // a verb that is really implemented would surface a Failed(...) error
    // envelope from that 500, never capability_unsupported and never the
    // bare not-wired ServiceUnavailable result, so the classification stays
    // honest even if a stub grows HTTP by accident.
    // ====================================================================

    public sealed record DriverCase(
        string Name,
        IReadOnlySet<PlatformCapability> Capabilities,
        IGitPlatformClient Client)
    {
        public override string ToString() => Name;
    }

    private sealed class Always500Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{}"),
            });
    }

    private static IGitPlatformClient GiteaStyleClient()
    {
        var http = new GiteaHttpClient(
            new HttpClient(new Always500Handler()),
            Guid.NewGuid(),
            "https://gitea.example",
            new GiteaAuth.BotToken("test-token"),
            new GiteaOAuth2TokenCache());
        return new GiteaPlatformClient(http, "gitea.example");
    }

    private static IEnumerable<DriverCase> DriverCases()
    {
        yield return new DriverCase(
            "GitHub",
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitHub),
            new GitHubPlatformClient(new NullGitHubActionsClient(), "github.com"));

        // Version 1.22 ≥ MinimumActionsVersion so nothing is narrowed away —
        // the fullest capability set the driver can compute.
        yield return new DriverCase(
            "Gitea",
            GiteaPlatformDriver.ComputeCapabilities(new Version(1, 22)),
            GiteaStyleClient());

        yield return new DriverCase(
            "Forgejo",
            ForgejoPlatformDriver.ComputeCapabilities(new Version(1, 22)),
            GiteaStyleClient());

        yield return new DriverCase(
            "GitLab",
            PlatformKindCapabilityMatrix.DefaultsFor(PlatformKind.GitLab),
            new GitLabPlatformClient(
                new GitLabHttpClient(
                    new HttpClient(new Always500Handler()),
                    new GitLabAuth.PersonalAccessToken("test-token"),
                    "https://gitlab.example"),
                NullLogger<GitLabPlatformClient>.Instance));

        yield return new DriverCase(
            "Null",
            new HashSet<PlatformCapability>(),
            NullGitPlatformDriver.Instance.Client);
    }

    // ====================================================================
    // The pinned, shrink-only exemption list.
    // ====================================================================

    private sealed record CapabilityLie(string DriverCase, PlatformCapability Capability, string Reason);

    /// <summary>
    /// Today's known lies, exactly. ONE entry: the GitHub matrix advertises
    /// PrLifecycle (Story 31-13 — the LIVE surface, GitHubIntegrationService,
    /// does perform the lifecycle) while the DRIVER stubs all six verbs.
    /// <b>Stage 2 of P1 removes this entry</b> when the GitHub driver absorbs
    /// the live REST/GraphQL bodies — at which point
    /// <c>Exemptions_areStillRealLies_notStaleEntries</c> forces the delete.
    /// </summary>
    private static readonly CapabilityLie[] KnownCapabilityLies =
    [
        new("GitHub", PlatformCapability.PrLifecycle,
            "matrix advertises the 31-13 lifecycle over a fully-stubbed driver; "
            + "the working implementation lives on GitHubIntegrationService until "
            + "Epic 31 P1 stage 2 moves it into Tamma.Platforms.GitHub"),
    ];

    /// <summary>
    /// The exemption pin's recorded high-water history, oldest first —
    /// strictly decreasing after the seed, the sweep-ratchet shape. Seeded at
    /// 1 (2026-08-07, Epic 31 P1 stage 1). It reaches 0 in P1 stage 2 and
    /// never grows: a NEW capability lie is not a reason to add an entry, it
    /// is the defect this fixture exists to catch.
    /// </summary>
    private static readonly int[] ExemptionPinHistory = [1];

    /// <summary>
    /// Membership pin — the ONLY entry this list may ever contain. A new
    /// (driver, capability) pair here must instead be fixed in the driver.
    /// </summary>
    private static readonly (string DriverCase, PlatformCapability Capability)[] AllowedExemptions =
    [
        ("GitHub", PlatformCapability.PrLifecycle),
    ];

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
            var answer = await probe.Invoke(driver.Client);

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
                        + "a stub is the GitHub lie this test pins; do not add an exemption, fix the driver.");
                }
            }
            else if (!advertised)
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
        // fails here because it is outside the pinned membership.
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
            "the exemption count is pinned; it may only shrink (P1 stage 2 takes it to zero)");
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
    }

    [Test]
    public async Task Exemptions_AreStillRealLies_NotStaleEntries()
    {
        // The staleness arm: the moment P1 stage 2 implements the GitHub
        // lifecycle verbs, this fails until the exemption entry is deleted
        // (and ExemptionPinHistory gains its 0) — the list drains instead of
        // rotting.
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
                var answer = await probe.Invoke(driver.Client);
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
        ]);
        CapabilityVerbs.Should().HaveCount(13,
            "6 lifecycle + 3 issue-lifecycle + 1 release + 1 review-comment-read + 2 commit-read "
            + "verbs; a new capability-gated verb must be added here in the same commit");
    }
}
