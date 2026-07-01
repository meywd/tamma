using System.IO.Compression;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Api.Services.AgentDispatch;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 (AC4) — the server-side collect aggregation (moved out of the
/// engine's former AgentResultCollectorService). Exercises the multi-read merge
/// (artifact / PR / compare / check runs) against a fake IGitHubActionsClient.
/// </summary>
[TestFixture]
public class ActionsResultAggregatorTests
{
    private const string Owner = "acme";
    private const string Name = "widgets";
    private const long RunId = 99;

    private static CollectAgentRunRequest Request(string conclusion = "success") => new()
    {
        BranchName = "tamma/issue-42",
        Conclusion = conclusion,
        AgentProvider = "claude-code",
        DurationSeconds = 123,
        CorrelationId = "sess_abc",
    };

    private static byte[] BuildArtifactZip(string resultJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("result.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(resultJson);
        }
        return ms.ToArray();
    }

    [Test]
    public async Task Aggregate_UsesArtifactWhenAvailable()
    {
        var fake = new FakeGitHubActionsClient();
        fake.ArtifactsByRunId[RunId] = new[] { new WorkflowRunArtifact(500, "tamma-result", 100, Expired: false) };
        fake.ArtifactBytes[500] = BuildArtifactZip(@"{
            ""success"": true, ""task"": ""implement"", ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"", ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [""a.ts""], ""pr_number"": 7, ""commit_sha"": ""abc"",
            ""tokens_used"": 1000, ""duration_seconds"": 60, ""agent_provider"": ""claude-code""
        }");

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request());

        result.Success.Should().BeTrue("mediation succeeded");
        result.AgentSuccess.Should().BeTrue();
        result.PrNumber.Should().Be(7);
        result.CommitSha.Should().Be("abc");
        result.TokensUsed.Should().Be(1000);
        result.FilesChanged.Should().ContainSingle().Which.Should().Be("a.ts");
        result.CredentialSource.Should().Be("installation");
    }

    [Test]
    public async Task Aggregate_FallsBackToCompare_WhenArtifactMissing()
    {
        var fake = new FakeGitHubActionsClient
        {
            Comparison = new BranchComparison(
                BaseSha: "base-sha", HeadSha: "head-sha",
                Files: new[] { new CompareFileChange("src/x.ts", "modified", 5, 3), new CompareFileChange("src/y.ts", "added", 20, 0) },
                Commits: new[] { new CompareCommit("commit1", "first"), new CompareCommit("commit2", "second") })
        };

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request("failure"));

        result.AgentSuccess.Should().BeFalse();
        result.CommitSha.Should().Be("head-sha");
        result.FilesChanged.Should().HaveCount(2);
        result.CommitsCount.Should().Be(2);
        result.ErrorMessage.Should().Contain("failure");
        result.ErrorMessage.Should().Contain("no result artifact");
    }

    [Test]
    public async Task Aggregate_IncludesPullRequestWhenFound()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[] { new PullRequestSummary(11, "PR", null, "https://github.com/acme/widgets/pull/11", "pr-sha", 2) }
        };

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request());

        result.PrNumber.Should().Be(11);
        result.PrUrl.Should().Be("https://github.com/acme/widgets/pull/11");
    }

    [Test]
    public async Task Aggregate_ComputesChecksPassed_AllSuccess()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[] { new PullRequestSummary(11, "PR", null, "url", "head-sha", 1) },
            CheckRuns = new[] { new CheckRunSummary("build", "completed", "success"), new CheckRunSummary("test", "completed", "success") }
        };

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request());

        result.ChecksPassed.Should().BeTrue();
    }

    [Test]
    public async Task Aggregate_ComputesChecksFailed_MixedStatus()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[] { new PullRequestSummary(11, "PR", null, "url", "h", 1) },
            CheckRuns = new[] { new CheckRunSummary("build", "completed", "success"), new CheckRunSummary("test", "completed", "failure") }
        };

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request());

        result.ChecksPassed.Should().BeFalse();
    }

    [Test]
    public async Task Aggregate_ReturnsNullChecks_WhenPending()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[] { new PullRequestSummary(11, "PR", null, "url", "h", 1) },
            CheckRuns = new[] { new CheckRunSummary("build", "in_progress", null) }
        };

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request());

        result.ChecksPassed.Should().BeNull();
    }

    [Test]
    public async Task Aggregate_IgnoresExpiredArtifacts()
    {
        var fake = new FakeGitHubActionsClient();
        fake.ArtifactsByRunId[RunId] = new[] { new WorkflowRunArtifact(500, "tamma-result", 100, Expired: true) };

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request());

        result.TokensUsed.Should().Be(0);
        result.AgentProvider.Should().Be("claude-code");
    }

    [Test]
    public async Task Aggregate_OversizedResultJsonInZip_IsRejected()
    {
        var hugePayload = new string('z', (int)(AgentResultArtifactParser.MaxResultJsonBytes + 1024));
        var json = $@"{{
            ""success"": true, ""task"": ""implement"", ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"", ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [], ""commit_sha"": ""abc123"", ""agent_log_summary"": ""{hugePayload}""
        }}";

        var fake = new FakeGitHubActionsClient();
        fake.ArtifactsByRunId[RunId] = new[] { new WorkflowRunArtifact(500, "tamma-result", 100, Expired: false) };
        fake.ArtifactBytes[500] = BuildArtifactZip(json);

        var agg = new ActionsResultAggregator(fake);
        var result = await agg.AggregateAsync(Owner, Name, RunId, Request("success"));

        result.AgentSuccess.Should().BeTrue("monitor said success; artifact was rejected but that doesn't flip it");
        result.TokensUsed.Should().Be(0, "artifact was rejected so no tokens carried over");
        result.AgentLogSummary.Should().BeNull("no artifact means no log summary");
    }
}
