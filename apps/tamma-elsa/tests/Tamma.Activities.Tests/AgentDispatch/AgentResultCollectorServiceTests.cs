using System.IO.Compression;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

[TestFixture]
public class AgentResultCollectorServiceTests
{
    private static AgentExecutionRequest MakeRequest() =>
        new(
            Repository: "acme/widgets",
            BranchName: "tamma/issue-42",
            IssueNumber: 42,
            IssueTitle: string.Empty,
            Task: "implement",
            PlanJson: string.Empty,
            SessionId: "sess_abc",
            AgentProvider: "claude-code",
            AgentConfigJson: null,
            WorkflowFileName: null,
            TimeoutMinutes: 0);

    private static AgentMonitorResult MonitorOk(string conclusion = "success") =>
        new(
            WorkflowRunId: 99,
            Status: "completed",
            Conclusion: conclusion,
            WorkflowRunUrl: "https://github.com/acme/widgets/actions/runs/99",
            DurationSeconds: 123,
            ArtifactsUrl: string.Empty);

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
    public void ParseResultJson_MapsAllFields()
    {
        var json = @"{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [""src/foo.ts"", ""src/bar.ts""],
            ""pr_number"": 7,
            ""commit_sha"": ""abc123"",
            ""error_message"": null,
            ""agent_log_summary"": ""done"",
            ""tokens_used"": 4321,
            ""duration_seconds"": 55,
            ""agent_provider"": ""claude-code"",
            ""agent_version"": ""1.2.3""
        }";

        var artifact = AgentResultCollectorService.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.Success.Should().BeTrue();
        artifact.FilesChanged.Should().HaveCount(2);
        artifact.PrNumber.Should().Be(7);
        artifact.CommitSha.Should().Be("abc123");
        artifact.TokensUsed.Should().Be(4321);
        artifact.AgentVersion.Should().Be("1.2.3");
    }

    [Test]
    public void ParseResultJson_ReturnsNullForInvalidJson()
    {
        var artifact = AgentResultCollectorService.ParseResultJson("{ broken ");
        artifact.Should().BeNull();
    }

    [Test]
    public async Task CollectAsync_UsesArtifactWhenAvailable()
    {
        var fake = new FakeGitHubActionsClient();
        fake.ArtifactsByRunId[99] = new[]
        {
            new WorkflowRunArtifact(Id: 500, Name: "tamma-result", SizeInBytes: 100, Expired: false)
        };
        fake.ArtifactBytes[500] = BuildArtifactZip(@"{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [""a.ts""],
            ""pr_number"": 7,
            ""commit_sha"": ""abc"",
            ""tokens_used"": 1000,
            ""duration_seconds"": 60,
            ""agent_provider"": ""claude-code""
        }");

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk());

        result.Success.Should().BeTrue();
        result.PrNumber.Should().Be(7);
        result.CommitSha.Should().Be("abc");
        result.TokensUsed.Should().Be(1000);
        result.FilesChanged.Should().ContainSingle().Which.Should().Be("a.ts");
    }

    [Test]
    public async Task CollectAsync_FallsBackToCompare_WhenArtifactMissing()
    {
        var fake = new FakeGitHubActionsClient
        {
            Comparison = new BranchComparison(
                BaseSha: "base-sha",
                HeadSha: "head-sha",
                Files: new[]
                {
                    new CompareFileChange("src/x.ts", "modified", 5, 3),
                    new CompareFileChange("src/y.ts", "added", 20, 0)
                },
                Commits: new[]
                {
                    new CompareCommit("commit1", "first"),
                    new CompareCommit("commit2", "second")
                })
        };

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk("failure"));

        result.Success.Should().BeFalse();
        result.CommitSha.Should().Be("head-sha");
        result.FilesChanged.Should().HaveCount(2);
        result.CommitsCount.Should().Be(2);
        result.ErrorMessage.Should().Contain("failure");
        result.ErrorMessage.Should().Contain("no result artifact");
    }

    [Test]
    public async Task CollectAsync_IncludesPullRequestWhenFound()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[]
            {
                new PullRequestSummary(
                    Number: 11, Title: "PR", Body: null,
                    HtmlUrl: "https://github.com/acme/widgets/pull/11",
                    HeadSha: "pr-sha", ChangedFiles: 2)
            }
        };

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk());

        result.PrNumber.Should().Be(11);
        result.PrUrl.Should().Be("https://github.com/acme/widgets/pull/11");
    }

    [Test]
    public async Task CollectAsync_ComputesChecksPassed_AllSuccess()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[]
            {
                new PullRequestSummary(11, "PR", null, "url", "head-sha", 1)
            },
            CheckRuns = new[]
            {
                new CheckRunSummary("build", "completed", "success"),
                new CheckRunSummary("test", "completed", "success")
            }
        };

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk());

        result.ChecksPassed.Should().BeTrue();
    }

    [Test]
    public async Task CollectAsync_ComputesChecksFailed_MixedStatus()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[] { new PullRequestSummary(11, "PR", null, "url", "h", 1) },
            CheckRuns = new[]
            {
                new CheckRunSummary("build", "completed", "success"),
                new CheckRunSummary("test", "completed", "failure")
            }
        };

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk());

        result.ChecksPassed.Should().BeFalse();
    }

    [Test]
    public async Task CollectAsync_ReturnsNullChecks_WhenPending()
    {
        var fake = new FakeGitHubActionsClient
        {
            Pulls = new[] { new PullRequestSummary(11, "PR", null, "url", "h", 1) },
            CheckRuns = new[]
            {
                new CheckRunSummary("build", "in_progress", null)
            }
        };

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk());

        result.ChecksPassed.Should().BeNull();
    }

    [Test]
    public async Task CollectAsync_IgnoresExpiredArtifacts()
    {
        var fake = new FakeGitHubActionsClient();
        fake.ArtifactsByRunId[99] = new[]
        {
            new WorkflowRunArtifact(500, "tamma-result", 100, Expired: true)
        };

        var svc = new AgentResultCollectorService(fake);
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk());

        result.TokensUsed.Should().Be(0);
        result.AgentProvider.Should().Be("claude-code");
    }

    // ================================================================
    // Review-session 2026-04-20 finding 6 — bounded artifact download
    // ================================================================

    [Test]
    public void ParseResultJson_ClampsAgentLogSummary_To32Kb()
    {
        var hugeLog = new string('x', 100_000); // 100 KB — over 32 KB cap
        var json = $@"{{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [],
            ""commit_sha"": ""abc123"",
            ""agent_log_summary"": ""{hugeLog}"",
            ""tokens_used"": 0,
            ""duration_seconds"": 1,
            ""agent_provider"": ""claude-code""
        }}";

        var artifact = AgentResultCollectorService.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.AgentLogSummary.Should().NotBeNull();
        artifact.AgentLogSummary!.Length.Should().Be(
            AgentResultCollectorService.MaxAgentLogSummaryChars,
            "agent_log_summary is clamped to 32 KB");
    }

    [Test]
    public void ParseResultJson_ClampsShortStringFields_To2Kb()
    {
        var hugeMessage = new string('y', 10_000); // 10 KB — over 2 KB cap
        var json = $@"{{
            ""success"": false,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [],
            ""commit_sha"": ""abc123"",
            ""error_message"": ""{hugeMessage}"",
            ""tokens_used"": 0,
            ""duration_seconds"": 1,
            ""agent_provider"": ""claude-code""
        }}";

        var artifact = AgentResultCollectorService.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.ErrorMessage.Should().NotBeNull();
        artifact.ErrorMessage!.Length.Should().Be(
            AgentResultCollectorService.MaxShortStringChars,
            "error_message is clamped to 2 KB");
    }

    [Test]
    public async Task CollectAsync_OversizedResultJsonInZip_IsRejected()
    {
        // Build a zip whose result.json is ~5 MB — over the 4 MB cap.
        var hugePayload = new string('z', (int)(AgentResultCollectorService.MaxResultJsonBytes + 1024));
        var json = $@"{{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [],
            ""commit_sha"": ""abc123"",
            ""agent_log_summary"": ""{hugePayload}""
        }}";

        var fake = new FakeGitHubActionsClient();
        fake.ArtifactsByRunId[99] = new[]
        {
            new WorkflowRunArtifact(500, "tamma-result", 100, Expired: false)
        };
        fake.ArtifactBytes[500] = BuildArtifactZip(json);

        var svc = new AgentResultCollectorService(fake);

        // Oversized result.json is rejected — the service falls back to
        // other data sources (PR lookup, compare API). No artifact data
        // propagates.
        var result = await svc.CollectAsync(MakeRequest(), MonitorOk("success"));

        result.Success.Should().BeTrue("monitor said success; artifact was rejected but that doesn't flip it");
        result.TokensUsed.Should().Be(0, "artifact was rejected so no tokens carried over");
        result.AgentLogSummary.Should().BeNull("no artifact means no log summary");
    }

    [Test]
    public void LimitedStream_ThrowsOnOverflow()
    {
        var data = new byte[8 * 1024]; // 8 KB
        using var src = new MemoryStream(data);
        using var limited = new LimitedStream(src, byteLimit: 4 * 1024); // 4 KB cap

        var buf = new byte[8 * 1024];
        Action act = () =>
        {
            var total = 0;
            int n;
            while ((n = limited.Read(buf, total, buf.Length - total)) > 0)
            {
                total += n;
            }
        };

        act.Should().Throw<ArtifactTooLargeException>();
    }

    [Test]
    public void LimitedStream_ReadsWithinLimit_Succeeds()
    {
        var data = new byte[2 * 1024]; // 2 KB
        using var src = new MemoryStream(data);
        using var limited = new LimitedStream(src, byteLimit: 4 * 1024); // 4 KB cap

        using var dest = new MemoryStream();
        limited.CopyTo(dest);

        dest.Length.Should().Be(2 * 1024);
    }
}
