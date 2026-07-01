using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 — the thin <see cref="AgentResultCollectorService"/> (wire→result
/// mapping) plus the pure <see cref="AgentResultArtifactParser"/> (relocated from
/// the former collector; still engine-side, still credential-free). The multi-read
/// aggregation moved server-side to <c>Tamma.Api</c>'s <c>ActionsResultAggregator</c>
/// (covered by <c>ActionsResultAggregatorTests</c> there).
/// </summary>
[TestFixture]
public class AgentResultCollectorServiceTests
{
    private static AgentExecutionRequest MakeRequest(Guid? tenantId = null) =>
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
            TimeoutMinutes: 0,
            TenantId: tenantId);

    private static AgentMonitorResult MonitorOk(string conclusion = "success") =>
        new(WorkflowRunId: 99, Status: "completed", Conclusion: conclusion,
            WorkflowRunUrl: "https://github.com/acme/widgets/actions/runs/99", DurationSeconds: 123, ArtifactsUrl: string.Empty);

    // ================================================================
    // Thin-client wire→result mapping (AC5) + tenant threading
    // ================================================================

    [Test]
    public async Task CollectAsync_CallsMediation_AndMapsAggregatedResult()
    {
        var tenant = Guid.NewGuid();
        var api = new FakeTammaApiClient
        {
            OnCollect = (_, _, _, _) => new AgentRunResultsApiResponse
            {
                Success = true,
                AgentSuccess = true,
                PrNumber = 7,
                PrUrl = "https://gh/pr/7",
                CommitSha = "abc",
                FilesChanged = new[] { "a.ts" },
                CommitsCount = 2,
                ChecksPassed = true,
                TokensUsed = 1000,
                DurationSeconds = 60,
                AgentProvider = "claude-code",
            }
        };
        var svc = new AgentResultCollectorService(api);

        var result = await svc.CollectAsync(MakeRequest(tenant), MonitorOk());

        result.Success.Should().BeTrue();
        result.PrNumber.Should().Be(7);
        result.CommitSha.Should().Be("abc");
        result.TokensUsed.Should().Be(1000);
        result.FilesChanged.Should().ContainSingle().Which.Should().Be("a.ts");
        result.ChecksPassed.Should().BeTrue();

        api.CollectCalls.Should().HaveCount(1);
        var call = api.CollectCalls[0];
        call.Repo.Should().Be("acme/widgets");
        call.RunId.Should().Be(99);
        call.TenantId.Should().Be(tenant.ToString());
        call.Request.Conclusion.Should().Be("success");
        call.Request.DurationSeconds.Should().Be(123);
    }

    [Test]
    public async Task CollectAsync_InvalidRepo_FailsWithoutCallingApi()
    {
        var api = new FakeTammaApiClient();
        var svc = new AgentResultCollectorService(api);

        var request = MakeRequest() with { Repository = "bad" };
        var result = await svc.CollectAsync(request, MonitorOk());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid repository format");
        api.CollectCalls.Should().BeEmpty();
    }

    [Test]
    public void MapResponse_NullResponse_FailsClosed()
    {
        var result = AgentResultCollectorService.MapResponse(null, "claude-code");
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unavailable");
        result.AgentProvider.Should().Be("claude-code");
    }

    [Test]
    public void MapResponse_AgentFailure_CarriesAgentSuccessFalse()
    {
        var result = AgentResultCollectorService.MapResponse(new AgentRunResultsApiResponse
        {
            Success = true,          // mediation succeeded
            AgentSuccess = false,    // agent's task failed
            ErrorMessage = "conclusion: failure; no result artifact found",
            CommitSha = "head-sha",
            FilesChanged = new[] { "x.ts", "y.ts" },
            CommitsCount = 2,
        }, "claude-code");

        result.Success.Should().BeFalse("the agent's own success rides in agentSuccess");
        result.CommitSha.Should().Be("head-sha");
        result.FilesChanged.Should().HaveCount(2);
        result.ErrorMessage.Should().Contain("failure");
    }

    // ================================================================
    // Outcome routing (review finding 2) — a MEDIATION/authorization failure
    // (collect never ran) is a hard Failed, checked BEFORE the Partial heuristic;
    // a genuine "ran but empty git state" stays a soft Partial.
    // ================================================================

    [Test]
    public void Route_NullResponseMediationOutage_RoutesToFailed_NotPartial()
    {
        var mediationFailure = AgentResultCollectorService.MapResponse(null, "claude-code");
        // Empty CommitSha + empty FilesChanged would trip the Partial heuristic —
        // the mediation-unavailable marker must win first and route to Failed.
        CollectAgentResultsActivity.Route(mediationFailure).Should().Be(CollectAgentResultsActivity.CollectRoute.Failed);
    }

    [Test]
    public void Route_GuardDenyMediationFailure_RoutesToFailed()
    {
        var denied = AgentResultCollectorService.MapResponse(
            new AgentRunResultsApiResponse
            {
                Success = false, // mediation failed (guard 403 rode as success:false or nulled body)
                FailureCode = "REPO_NOT_AUTHORIZED",
                FailureReason = "repo not authorized for tenant",
            },
            "claude-code");

        CollectAgentResultsActivity.Route(denied).Should().Be(CollectAgentResultsActivity.CollectRoute.Failed);
        denied.ErrorMessage.Should().StartWith(AgentResultCollectorService.CollectionUnavailableMarker);
    }

    [Test]
    public void Route_GenuineEmptyGitState_StaysPartial()
    {
        // Mediation SUCCEEDED, agent ran, but no commit/files were read → soft Partial.
        var partial = AgentResultCollectorService.MapResponse(
            new AgentRunResultsApiResponse
            {
                Success = true,
                AgentSuccess = true,
                CommitSha = string.Empty,
                FilesChanged = Array.Empty<string>(),
            },
            "claude-code");

        CollectAgentResultsActivity.Route(partial).Should().Be(CollectAgentResultsActivity.CollectRoute.Partial);
    }

    [Test]
    public void Route_FullResult_IsCollected()
    {
        var full = AgentResultCollectorService.MapResponse(
            new AgentRunResultsApiResponse
            {
                Success = true,
                AgentSuccess = true,
                CommitSha = "abc",
                FilesChanged = new[] { "a.ts" },
            },
            "claude-code");

        CollectAgentResultsActivity.Route(full).Should().Be(CollectAgentResultsActivity.CollectRoute.Collected);
    }

    // ================================================================
    // Pure result.json parser (relocated to AgentResultArtifactParser)
    // ================================================================

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

        var artifact = AgentResultArtifactParser.ParseResultJson(json);

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
        AgentResultArtifactParser.ParseResultJson("{ broken ").Should().BeNull();
    }

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

        var artifact = AgentResultArtifactParser.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.AgentLogSummary!.Length.Should().Be(
            AgentResultArtifactParser.MaxAgentLogSummaryChars, "agent_log_summary is clamped to 32 KB");
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

        var artifact = AgentResultArtifactParser.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.ErrorMessage!.Length.Should().Be(
            AgentResultArtifactParser.MaxShortStringChars, "error_message is clamped to 2 KB");
    }

    [Test]
    public void ParseResultJson_ClampsFilesChangedCount_To2000()
    {
        // Review finding 6: a malicious result.json with ~1M tiny file entries must not
        // balloon the allocation / JSONB column — the COUNT is capped, not just each entry.
        var files = string.Join(",", Enumerable.Range(0, 5000).Select(i => $"\"f{i}.ts\""));
        var json = $@"{{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [{files}],
            ""commit_sha"": ""abc123"",
            ""tokens_used"": 0,
            ""duration_seconds"": 1,
            ""agent_provider"": ""claude-code""
        }}";

        var artifact = AgentResultArtifactParser.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.FilesChanged.Length.Should().Be(
            AgentResultArtifactParser.MaxFilesChangedCount, "files_changed count is capped at 2000");
    }

    [Test]
    public void ParseResultJson_ClampsTokensUsed_ToCeiling()
    {
        // A poisoned tokens_used must not corrupt cost/analytics — clamp to the ceiling.
        var json = $@"{{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [],
            ""commit_sha"": ""abc123"",
            ""tokens_used"": {int.MaxValue},
            ""duration_seconds"": 1,
            ""agent_provider"": ""claude-code""
        }}";

        var artifact = AgentResultArtifactParser.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.TokensUsed.Should().Be(
            AgentResultArtifactParser.MaxTokensUsed, "tokens_used is clamped to the 100M ceiling");
    }

    [Test]
    public void ParseResultJson_ClampsNegativeTokensUsed_ToZero()
    {
        var json = @"{
            ""success"": true,
            ""task"": ""implement"",
            ""issue_number"": 42,
            ""branch_name"": ""tamma/issue-42"",
            ""tamma_session_id"": ""sess_abc"",
            ""files_changed"": [],
            ""commit_sha"": ""abc123"",
            ""tokens_used"": -500,
            ""duration_seconds"": 1,
            ""agent_provider"": ""claude-code""
        }";

        var artifact = AgentResultArtifactParser.ParseResultJson(json);

        artifact.Should().NotBeNull();
        artifact!.TokensUsed.Should().Be(0, "a negative tokens_used is clamped to 0");
    }

    // ================================================================
    // LimitedStream (stays in Tamma.Activities — used by the Octokit client)
    // ================================================================

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
