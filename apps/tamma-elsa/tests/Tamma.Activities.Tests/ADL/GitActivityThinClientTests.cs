using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Story 38-1 (AC5/AC9) — the five ADL git activities are now THIN
/// <c>TammaApiClient</c> clients. These tests cover the wire-response →
/// workflow-variable mapping (so the surrounding ADL workflows are unchanged),
/// the fail-closed null-response path, and the cutover proof: ZERO
/// <see cref="IGitHubIntegrationService"/> injections remain in
/// <c>Tamma.Activities</c> (no constructor param, no field, no
/// <c>GetService&lt;IGitHubIntegrationService&gt;</c>) — the engine can no longer
/// resolve the credential-holding vendor service.
/// </summary>
[TestFixture]
public class GitActivityThinClientTests
{
    // ===================================================================
    // CreateBranch mapping (AC5)
    // ===================================================================

    [Test]
    public void CreateBranch_Map_Created_ProjectsBranchOutputs()
    {
        var outcome = CreateBranchActivity.MapResponse(new GitCallResponse
        {
            Success = true, Outcome = "Created", BranchRef = "adl/7-thing", BaseSha = "sha1", ConflictResolved = true,
        });

        outcome.Outcome.Should().Be("Created");
        outcome.BranchName.Should().Be("adl/7-thing");
        outcome.BaseSha.Should().Be("sha1");
        outcome.ConflictResolved.Should().BeTrue();
    }

    [Test]
    public void CreateBranch_Map_Failure_ProjectsErrorCode()
    {
        var outcome = CreateBranchActivity.MapResponse(new GitCallResponse
        {
            Success = false, Outcome = "Error", FailureCode = "GIT_CONFLICT", FailureReason = "branch exists",
        });

        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("GIT_CONFLICT");
        outcome.Error.Should().Be("branch exists");
    }

    [Test]
    public void CreateBranch_Map_NullResponse_FailsClosed()
    {
        var outcome = CreateBranchActivity.MapResponse(null);
        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("git-mediation-unavailable");
    }

    // ===================================================================
    // CreatePullRequest mapping (AC5)
    // ===================================================================

    [Test]
    public void CreatePr_Map_Created_And_Updated()
    {
        var created = CreatePullRequestActivity.MapResponse(new GitCallResponse
        { Success = true, Outcome = "Created", PrNumber = 42, PrUrl = "u", IsDraft = false });
        created.Outcome.Should().Be("Created");
        created.PrNumber.Should().Be(42);
        created.Reused.Should().BeFalse();

        var updated = CreatePullRequestActivity.MapResponse(new GitCallResponse
        { Success = true, Outcome = "Updated", PrNumber = 42, PrUrl = "u", IsDraft = true });
        updated.Outcome.Should().Be("Updated");
        updated.Reused.Should().BeTrue();
        updated.IsDraft.Should().BeTrue();
    }

    [Test]
    public void CreatePr_Map_NullResponse_FailsClosed()
    {
        var outcome = CreatePullRequestActivity.MapResponse(null);
        outcome.Outcome.Should().Be("Error");
        outcome.ErrorCode.Should().Be("git-mediation-unavailable");
    }

    // ===================================================================
    // Merge mapping (AC5) — preserves the Merged / MergedWithWarnings / Error edge
    // ===================================================================

    [Test]
    public void Merge_Map_Merged_Clean()
    {
        var outcome = MergePullRequestActivity.MapResponse(new GitCallResponse
        {
            Success = true, Outcome = "Merged", Merged = true, MergeSha = "sha", IssueClosed = true, BranchDeleted = true, AlreadyMerged = false,
        });

        outcome.Outcome.Should().Be("Merged");
        outcome.MergeSha.Should().Be("sha");
        outcome.IssueClosed.Should().BeTrue();
        outcome.BranchDeleted.Should().BeTrue();
        outcome.MergeSucceeded.Should().BeTrue();
    }

    [Test]
    public void Merge_Map_MergedWithWarnings_CarriesWarnings()
    {
        var outcome = MergePullRequestActivity.MapResponse(new GitCallResponse
        {
            Success = true, Outcome = "MergedWithWarnings", Merged = true, MergeSha = "sha",
            IssueClosed = false, BranchDeleted = true, FailureReason = "issue-close-failed: 500",
        });

        outcome.Outcome.Should().Be("MergedWithWarnings");
        outcome.Partial.Should().BeTrue();
        outcome.FailureReason.Should().Contain("issue-close-failed");
    }

    [Test]
    public void Merge_Map_Error_ProjectsFailureCode()
    {
        var outcome = MergePullRequestActivity.MapResponse(new GitCallResponse
        { Success = false, Outcome = "Error", FailureCode = "NOT_MERGEABLE", FailureReason = "closed" });

        outcome.Outcome.Should().Be("Error");
        outcome.FailureCode.Should().Be("NOT_MERGEABLE");
        outcome.MergeSucceeded.Should().BeFalse();
    }

    [Test]
    public void Merge_Map_NullResponse_FailsClosed()
    {
        var outcome = MergePullRequestActivity.MapResponse(null);
        outcome.Outcome.Should().Be("Error");
        outcome.MergeSucceeded.Should().BeFalse();
    }

    // ===================================================================
    // UpdateIssue mapping (AC5)
    // ===================================================================

    [Test]
    public void UpdateIssue_Map_Success_And_Failure_And_Null()
    {
        UpdateIssueStatusActivity.MapResponse(new GitCallResponse { Success = true }).Success.Should().BeTrue();

        var fail = UpdateIssueStatusActivity.MapResponse(new GitCallResponse
        { Success = false, FailureCode = "PLATFORM_ERROR", FailureReason = "403" });
        fail.Success.Should().BeFalse();
        fail.ErrorCode.Should().Be("PLATFORM_ERROR");

        UpdateIssueStatusActivity.MapResponse(null).Success.Should().BeFalse();
    }

    // ===================================================================
    // Cutover proof (AC9) — zero IGitHubIntegrationService injections
    // ===================================================================

    private static readonly Type[] GitActivityTypes =
    {
        typeof(CreateBranchActivity),
        typeof(CreatePullRequestActivity),
        typeof(MergePullRequestActivity),
        typeof(UpdateIssueStatusActivity),
        typeof(AnalyzeReviewActivity),
    };

    [Test]
    public void NoGitActivity_HasIGitHubIntegrationServiceConstructorParameter()
    {
        foreach (var type in GitActivityTypes)
        {
            foreach (var ctor in type.GetConstructors())
            {
                ctor.GetParameters()
                    .Any(p => p.ParameterType.Name == "IGitHubIntegrationService")
                    .Should().BeFalse($"{type.Name} must not inject IGitHubIntegrationService via its constructor");
            }
        }
    }

    [Test]
    public void NoGitActivity_HasIGitHubIntegrationServiceField()
    {
        foreach (var type in GitActivityTypes)
        {
            type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(f => f.FieldType.Name == "IGitHubIntegrationService")
                .Should().BeFalse($"{type.Name} must hold no IGitHubIntegrationService field");
        }
    }

    [Test]
    public void NoActivitySource_ResolvesIGitHubIntegrationServiceFromDi()
    {
        // Belt-and-suspenders over the reflection checks: a source scan catches the
        // context.GetService<IGitHubIntegrationService>() service-locator pattern (a
        // method call, invisible to reflection). Zero occurrences across the WHOLE
        // Tamma.Activities tree — no activity may resolve the credential-holding
        // vendor service from DI after the pivot.
        var activitiesDir = FindActivitiesRoot();
        activitiesDir.Should().NotBeNull("the Tamma.Activities source root should be locatable from the test run");

        var serviceLocatorOffenders = Directory.EnumerateFiles(activitiesDir!, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("GetService<IGitHubIntegrationService>")
                    || text.Contains("GetRequiredService<IGitHubIntegrationService>");
            })
            .Select(Path.GetFileName)
            .ToList();
        serviceLocatorOffenders.Should().BeEmpty("no activity may resolve IGitHubIntegrationService from DI after the 38-1 cutover");

        // The five cutover activities additionally hold no `_github` field.
        var adlDir = Path.Combine(activitiesDir!, "ADL");
        foreach (var name in new[]
        {
            "CreateBranchActivity.cs", "CreatePullRequestActivity.cs", "MergePullRequestActivity.cs",
            "UpdateIssueStatusActivity.cs", "AnalyzeReviewActivity.cs",
        })
        {
            var text = File.ReadAllText(Path.Combine(adlDir, name));
            text.Should().NotContain("_github", $"{name} must hold no IGitHubIntegrationService field after the cutover");
        }
    }

    private static string? FindActivitiesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tamma.Activities");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
