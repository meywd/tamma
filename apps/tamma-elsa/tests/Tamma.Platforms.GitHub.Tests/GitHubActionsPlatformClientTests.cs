using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — unit tests for the REAL
/// <see cref="GitHubActionsPlatformClient"/>: all 5 verbs over
/// scripted HTTP, the dispatch→run-id correlation (the pollable-RunId
/// requirement), and the 4 MB artifact cap.
/// </summary>
[TestFixture]
public sealed class GitHubActionsPlatformClientTests
{
    private const string Api = "https://api.github.com";

    private static (GitHubActionsPlatformClient Client, FakeHttpMessageHandler Handler) Build(
        long maxArtifactBytes = GitHubActionsPlatformClient.DefaultMaxArtifactBytes,
        int probeAttempts = 3)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new GitHubHttpClient(
            new HttpClient(handler), Api, new GitHubAuth.Pat("test-token"));
        var client = new GitHubActionsPlatformClient(
            http,
            maxArtifactBytes: maxArtifactBytes,
            dispatchProbeAttempts: probeAttempts,
            dispatchProbeDelay: TimeSpan.Zero);
        return (client, handler);
    }

    private static WorkflowDispatchRequest Dispatch(string? file = "ci.yml") =>
        new("main", file, new Dictionary<string, string> { ["issue"] = "42" });

    // ================================================================
    // Dispatch — run-id correlation
    // ================================================================

    [Test]
    public async Task Dispatch_returns_pollable_run_id_correlated_from_workflow_runs()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NoContent, string.Empty);
        handler.EnqueueJson(HttpMethod.Get,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/runs",
            HttpStatusCode.OK,
            """
            { "total_count": 2, "workflow_runs": [
                { "id": 900, "status": "queued", "conclusion": null, "html_url": "u900",
                  "event": "workflow_dispatch", "head_branch": "main",
                  "created_at": "2026-08-07T10:00:05Z", "updated_at": "2026-08-07T10:00:05Z" },
                { "id": 899, "status": "completed", "conclusion": "success", "html_url": "u899",
                  "event": "workflow_dispatch", "head_branch": "main",
                  "created_at": "2026-08-07T09:00:00Z", "updated_at": "2026-08-07T09:10:00Z" } ] }
            """);

        var result = await client.DispatchWorkflowAsync("o", "r", Dispatch());

        var run = result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>().Subject.Value;
        run.RunId.Should().Be("900", "the NEWEST run of the dispatched workflow+ref is returned");
        run.RunId.Should().NotBeNullOrEmpty("the placeholder-empty-RunId behavior is the bug this stage removed");
        // Correlation listing is scoped to the workflow file + ref + event.
        var listUrl = handler.Requests.Single(r => r.Url.Contains("/runs?")).Url;
        listUrl.Should().Contain("/actions/workflows/ci.yml/runs")
            .And.Contain("branch=main")
            .And.Contain("event=workflow_dispatch")
            .And.Contain("created=");
    }

    [Test]
    public async Task Dispatch_retries_run_listing_until_a_run_appears()
    {
        var (client, handler) = Build(probeAttempts: 3);
        handler.EnqueueJson(HttpMethod.Post,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NoContent, string.Empty);
        // First probe: empty; second probe: run visible.
        handler.EnqueueJson(HttpMethod.Get,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/runs",
            HttpStatusCode.OK, """{ "total_count": 0, "workflow_runs": [] }""");
        handler.EnqueueJson(HttpMethod.Get,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/runs",
            HttpStatusCode.OK,
            """
            { "total_count": 1, "workflow_runs": [
                { "id": 901, "status": "queued", "conclusion": null, "html_url": "u",
                  "event": "workflow_dispatch", "head_branch": "main",
                  "created_at": "2026-08-07T10:00:05Z", "updated_at": "2026-08-07T10:00:05Z" } ] }
            """);

        var result = await client.DispatchWorkflowAsync("o", "r", Dispatch());

        result.GetValueOrDefault()!.RunId.Should().Be("901");
    }

    [Test]
    public async Task Dispatch_with_no_correlatable_run_returns_typed_unknown_not_a_placeholder()
    {
        var (client, handler) = Build(probeAttempts: 2);
        handler.EnqueueJson(HttpMethod.Post,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NoContent, string.Empty);
        handler.EnqueueRepeating(HttpMethod.Get,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/runs",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "total_count": 0, "workflow_runs": [] }"""),
            });

        var result = await client.DispatchWorkflowAsync("o", "r", Dispatch());

        // At-least-once semantics: the dispatch DID happen; the driver
        // reports the correlation gap instead of minting an unpollable
        // empty RunId.
        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.Unknown>()
            // Epic 31 review (F-medium) — the message must carry the SHARED
            // prefix both mediation planes special-case as
            // success-without-a-correlated-run.
            .Which.Reason.Should().StartWith(PlatformErrorText.DispatchAcceptedPrefix);
    }

    // ── Epic 31 review (F-medium) — a fully-qualified refs/heads/ ref
    //    dispatches fine but the runs list's branch= filter takes a BARE
    //    branch name: without normalization the new run NEVER correlated. ──

    [Test]
    public async Task Dispatch_qualifiedHeadsRef_NormalizesTheBranchFilter()
    {
        var (client, handler) = Build(probeAttempts: 1);
        handler.EnqueueJson(HttpMethod.Post,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NoContent, string.Empty);
        handler.EnqueueJson(HttpMethod.Get,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/runs",
            HttpStatusCode.OK,
            """
            { "total_count": 1, "workflow_runs": [
                { "id": 902, "status": "queued", "conclusion": null, "html_url": "u",
                  "event": "workflow_dispatch", "head_branch": "feat/x",
                  "created_at": "2026-08-07T10:00:05Z", "updated_at": "2026-08-07T10:00:05Z" } ] }
            """);

        var result = await client.DispatchWorkflowAsync("o", "r",
            new WorkflowDispatchRequest("refs/heads/feat/x", "ci.yml",
                new Dictionary<string, string>()));

        result.GetValueOrDefault()!.RunId.Should().Be("902");
        var listUrl = handler.Requests.Single(r => r.Url.Contains("/runs?")).Url;
        listUrl.Should().Contain("branch=feat%2Fx",
            "the runs list filter takes the bare branch name — the qualified ref never matches");
        listUrl.Should().NotContain("refs%2Fheads");
    }

    [Test]
    public void NormalizeBranchFilter_StripsHeadsPrefix_LeavesEverythingElse()
    {
        GitHubActionsPlatformClient.NormalizeBranchFilter("refs/heads/feat/x").Should().Be("feat/x");
        GitHubActionsPlatformClient.NormalizeBranchFilter("main").Should().Be("main");
        GitHubActionsPlatformClient.NormalizeBranchFilter("refs/tags/v1").Should().Be("refs/tags/v1",
            "only the heads prefix is a branch — a tag ref is left for the platform to reject");
    }

    [Test]
    public async Task Dispatch_without_workflow_file_is_invalid_request()
    {
        var (client, _) = Build();

        var result = await client.DispatchWorkflowAsync("o", "r", Dispatch(file: null));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("workflow_file_required");
    }

    [Test]
    public async Task Dispatch_maps_404_workflow_to_NotFound()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post,
            $"{Api}/repos/o/r/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NotFound, """{ "message": "Not Found" }""");

        var result = await client.DispatchWorkflowAsync("o", "r", Dispatch());

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.NotFound>();
    }

    // ================================================================
    // GetRunStatus / ListRunJobs / CancelRun
    // ================================================================

    [Test]
    public async Task GetRunStatus_maps_run()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/actions/runs/900",
            HttpStatusCode.OK,
            """
            { "id": 900, "status": "completed", "conclusion": "success", "html_url": "u",
              "created_at": "2026-08-07T10:00:00Z", "updated_at": "2026-08-07T10:09:00Z",
              "run_started_at": "2026-08-07T10:00:30Z" }
            """);

        var result = await client.GetRunStatusAsync("o", "r", "900");

        var run = result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>().Subject.Value;
        run.Status.Should().Be("completed");
        run.Conclusion.Should().Be("success");
        run.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task GetRunStatus_rejects_non_numeric_run_id_without_http()
    {
        var (client, handler) = Build();

        var result = await client.GetRunStatusAsync("o", "r", "not-a-number");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("invalid_run_id");
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task ListRunJobs_maps_jobs()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r/actions/runs/900/jobs",
            HttpStatusCode.OK,
            """
            { "total_count": 2, "jobs": [
                { "id": 1, "name": "build", "status": "completed", "conclusion": "success" },
                { "id": 2, "name": "test", "status": "in_progress", "conclusion": null } ] }
            """);

        var result = await client.ListRunJobsAsync("o", "r", "900");

        var jobs = result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<WorkflowJob>>.Ok>().Subject.Value;
        jobs.Should().HaveCount(2);
        jobs[0].Should().Be(new WorkflowJob("1", "build", "completed", "success", null));
    }

    [Test]
    public async Task CancelRun_posts_cancel()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/actions/runs/900/cancel",
            HttpStatusCode.Accepted, string.Empty);

        var result = await client.CancelRunAsync("o", "r", "900");

        result.Should().BeOfType<PlatformResult<bool>.Ok>().Which.Value.Should().BeTrue();
    }

    [Test]
    public async Task CancelRun_on_already_completed_run_is_noop_success()
    {
        var (client, handler) = Build();
        // GitHub answers 409 "Cannot cancel a workflow run that is completed."
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/repos/o/r/actions/runs/900/cancel",
            HttpStatusCode.Conflict,
            """{ "message": "Cannot cancel a workflow run that is completed." }""");

        var result = await client.CancelRunAsync("o", "r", "900");

        result.Should().BeOfType<PlatformResult<bool>.Ok>().Which.Value.Should().BeTrue();
    }

    // ================================================================
    // DownloadArtifact — 4 MB cap
    // ================================================================

    [Test]
    public async Task DownloadArtifact_streams_zip_bytes()
    {
        var (client, handler) = Build();
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        handler.Enqueue(HttpMethod.Get, $"{Api}/repos/o/r/actions/artifacts/77/zip",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });

        var result = await client.DownloadArtifactAsync("o", "r", "77");

        using var stream = result.Should().BeOfType<PlatformResult<Stream>.Ok>().Subject.Value;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload);
    }

    [Test]
    public async Task DownloadArtifact_enforces_byte_cap()
    {
        var (client, handler) = Build(maxArtifactBytes: 16);
        handler.Enqueue(HttpMethod.Get, $"{Api}/repos/o/r/actions/artifacts/77/zip",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[64]),
            });

        var result = await client.DownloadArtifactAsync("o", "r", "77");

        using var stream = result.Should().BeOfType<PlatformResult<Stream>.Ok>().Subject.Value;
        var act = async () =>
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
        };
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(GitHubActionsPlatformClient.BoundedReadStream.TooLargeMessage);
    }

    [Test]
    public async Task DownloadArtifact_rejects_non_numeric_artifact_id_without_http()
    {
        var (client, handler) = Build();

        var result = await client.DownloadArtifactAsync("o", "r", "abc");

        result.Should().BeOfType<PlatformResult<Stream>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("invalid_artifact_id");
        handler.Requests.Should().BeEmpty();
    }
}
