using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.Integration;

/// <summary>
/// Story 38 (Phase 2, Batch A) — the eight domain activities that used to read git
/// commits / file-changes and trigger CI directly via the co-hosted, credential-holding
/// composite <see cref="IIntegrationService"/> now route through the thin
/// <see cref="TammaApiClient"/> → the git/CI mediation endpoints (the engine holds no
/// git token). These tests cover: (1) the wire-response → composite-model projection
/// (<see cref="GitMediationMapping"/>) so the surrounding workflows are unchanged, and
/// (2) the per-activity cutover proof — none of the eight injects
/// <see cref="IIntegrationService"/> via a constructor param or a field.
/// </summary>
[TestFixture]
public class IntegrationServiceCutoverTests
{
    // ===================================================================
    // Wire-response → composite-model mapping (GitMediationMapping)
    // ===================================================================

    [Test]
    public void ToCommits_ProjectsAllFields()
    {
        var ts = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var commits = GitMediationMapping.ToCommits(new[]
        {
            new GitCommitSummaryDto
            {
                Sha = "abc", Message = "msg", Author = "dev", Timestamp = ts,
                Additions = 3, Deletions = 1, Files = new[] { "a.cs", "b.cs" },
            },
        });

        commits.Should().HaveCount(1);
        var c = commits[0];
        c.Sha.Should().Be("abc");
        c.Message.Should().Be("msg");
        c.Author.Should().Be("dev");
        c.Timestamp.Should().Be(ts);
        c.Additions.Should().Be(3);
        c.Deletions.Should().Be(1);
        c.Files.Should().BeEquivalentTo("a.cs", "b.cs");
    }

    [Test]
    public void ToCommits_NullOrEmpty_YieldsEmptyList()
    {
        GitMediationMapping.ToCommits(null).Should().BeEmpty();
        GitMediationMapping.ToCommits(Array.Empty<GitCommitSummaryDto>()).Should().BeEmpty();
    }

    [Test]
    public void ToFileChanges_ProjectsAllFields()
    {
        var changes = GitMediationMapping.ToFileChanges(new[]
        {
            new GitFileChangeDto { FilePath = "src/x.cs", ChangeType = "modified", Additions = 5, Deletions = 2 },
        });

        changes.Should().HaveCount(1);
        changes[0].FilePath.Should().Be("src/x.cs");
        changes[0].ChangeType.Should().Be("modified");
        changes[0].Additions.Should().Be(5);
        changes[0].Deletions.Should().Be(2);
    }

    [Test]
    public void ToFileChanges_Null_YieldsEmptyList()
        => GitMediationMapping.ToFileChanges(null).Should().BeEmpty();

    [Test]
    public void ToTestRun_ProjectsCounts_AndLeavesFailedDetailsEmpty()
    {
        var run = GitMediationMapping.ToTestRun(new CiTestRunDto
        {
            RunId = "r1", Status = "completed", TotalTests = 10,
            PassedTests = 8, FailedTests = 2, SkippedTests = 0, CoveragePercentage = 82.5,
        });

        run.RunId.Should().Be("r1");
        run.Status.Should().Be("completed");
        run.TotalTests.Should().Be(10);
        run.PassedTests.Should().Be(8);
        run.FailedTests.Should().Be(2);
        run.CoveragePercentage.Should().Be(82.5);
        // The CI-mediation endpoint returns aggregate counts only, never per-test detail.
        run.FailedTestDetails.Should().BeEmpty();
    }

    [Test]
    public void ToTestRun_Null_YieldsEmptyResult()
    {
        var run = GitMediationMapping.ToTestRun(null);
        run.TotalTests.Should().Be(0);
        run.FailedTestDetails.Should().BeEmpty();
    }

    [Test]
    public void ToBuildStatus_ProjectsFields_AndLeavesErrorNull()
    {
        var started = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        var finished = new DateTime(2026, 7, 1, 10, 5, 0, DateTimeKind.Utc);
        var status = GitMediationMapping.ToBuildStatus(new CiBuildStatusDto
        {
            Status = "Success", BuildUrl = "https://ci/run/1", StartedAt = started, FinishedAt = finished,
        });

        status.Status.Should().Be("Success");
        status.BuildUrl.Should().Be("https://ci/run/1");
        status.StartedAt.Should().Be(started);
        status.FinishedAt.Should().Be(finished);
        // The CI-mediation build-status DTO carries no Error field — Error stays null.
        status.Error.Should().BeNull();
    }

    [Test]
    public void ToBuildStatus_Null_YieldsEmptyStatus()
    {
        var status = GitMediationMapping.ToBuildStatus(null);
        status.Status.Should().BeEmpty();
        status.Error.Should().BeNull();
    }

    // ===================================================================
    // Cutover proof — zero IIntegrationService injections in the eight
    // ===================================================================

    private static readonly Type[] CutoverActivityTypes =
    {
        // Batch A (8)
        typeof(Tamma.Activities.Blocker.CollectGitActivityActivity),
        typeof(Tamma.Activities.Blocker.CollectInactivityActivity),
        typeof(Tamma.Activities.AI.ContextGatheringActivity),
        typeof(Tamma.Activities.Integration.GitHubActivity),
        typeof(Tamma.Activities.Review.CreatePRActivity),
        typeof(Tamma.Activities.Context.FetchFileContentsActivity),
        typeof(Tamma.Activities.Context.FetchRecentCommitsActivity),
        typeof(Tamma.Activities.Mentorship.CodeReviewActivity),
        // Batch B (9) — 7 mediated + 2 dead-injection removals
        typeof(Tamma.Activities.Blocker.CollectCIStatusActivity),
        typeof(Tamma.Activities.Context.FetchTestResultsActivity),
        typeof(Tamma.Activities.Debug.CollectGitHistoryActivity),
        typeof(Tamma.Activities.Debug.CollectTestResultsActivity),
        typeof(Tamma.Activities.Debug.CollectRelevantCodeActivity),
        typeof(Tamma.Activities.Mentorship.QualityGateCheckActivity),
        typeof(Tamma.Activities.Mentorship.MonitorImplementationActivity),
        typeof(Tamma.Activities.Context.FetchSimilarPatternsActivity),
        typeof(Tamma.Activities.Blocker.CollectCommunicationActivity),
    };

    /// <summary>
    /// The subset that actively mediates a git/CI read through the thin client — these
    /// must hold a <see cref="TammaApiClient"/> field. Excludes
    /// <c>FetchSimilarPatternsActivity</c> and <c>CollectCommunicationActivity</c>, whose
    /// composite injection was DEAD (no call site) and was removed outright without
    /// adding a mediation client.
    /// </summary>
    private static readonly Type[] MediatedActivityTypes =
    {
        typeof(Tamma.Activities.Blocker.CollectGitActivityActivity),
        typeof(Tamma.Activities.Blocker.CollectInactivityActivity),
        typeof(Tamma.Activities.AI.ContextGatheringActivity),
        typeof(Tamma.Activities.Integration.GitHubActivity),
        typeof(Tamma.Activities.Review.CreatePRActivity),
        typeof(Tamma.Activities.Context.FetchFileContentsActivity),
        typeof(Tamma.Activities.Context.FetchRecentCommitsActivity),
        typeof(Tamma.Activities.Mentorship.CodeReviewActivity),
        typeof(Tamma.Activities.Blocker.CollectCIStatusActivity),
        typeof(Tamma.Activities.Context.FetchTestResultsActivity),
        typeof(Tamma.Activities.Debug.CollectGitHistoryActivity),
        typeof(Tamma.Activities.Debug.CollectTestResultsActivity),
        typeof(Tamma.Activities.Debug.CollectRelevantCodeActivity),
        typeof(Tamma.Activities.Mentorship.QualityGateCheckActivity),
        typeof(Tamma.Activities.Mentorship.MonitorImplementationActivity),
    };

    [Test]
    [TestCaseSource(nameof(CutoverActivityTypes))]
    public void CutoverActivity_InjectsNoIntegrationService(Type activityType)
    {
        foreach (var ctor in activityType.GetConstructors())
        {
            ctor.GetParameters()
                .Any(p => IsIntegrationServiceType(p.ParameterType))
                .Should().BeFalse(
                    $"{activityType.Name} must not inject the credential-holding IIntegrationService (ctor)");
        }

        activityType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => IsIntegrationServiceType(f.FieldType))
            .Should().BeFalse(
                $"{activityType.Name} must hold no IIntegrationService field after the 38 Phase 2 cutover");
    }

    [Test]
    [TestCaseSource(nameof(MediatedActivityTypes))]
    public void CutoverActivity_InjectsTammaApiClient(Type activityType)
    {
        activityType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => typeof(TammaApiClient).IsAssignableFrom(f.FieldType))
            .Should().BeTrue(
                $"{activityType.Name} must hold a TammaApiClient field (the mediation thin client)");
    }

    private static bool IsIntegrationServiceType(Type t) =>
        typeof(IIntegrationService).IsAssignableFrom(t)
        || typeof(IGitHubIntegrationService).IsAssignableFrom(t)
        || typeof(ICIIntegrationService).IsAssignableFrom(t);
}
