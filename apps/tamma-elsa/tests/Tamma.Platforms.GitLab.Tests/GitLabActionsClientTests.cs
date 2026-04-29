using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab;
using Tamma.Platforms.GitLab.Tests.Support;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class GitLabActionsClientTests
{
    [Test]
    public async Task DispatchWorkflowAsync_posts_pipeline_with_variables()
    {
        var (client, handler) = TestFactory.BuildActions();
        string? capturedBody = null;
        handler.AddRoute(HttpMethod.Post, "/projects/g%2Fp/pipeline", req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                """{"id":42,"status":"pending","ref":"main","web_url":"https://gitlab.example.com/g/p/-/pipelines/42","created_at":"2026-01-01T00:00:00Z","source":"api"}""");
        });

        var result = await client.DispatchWorkflowAsync(
            "g", "p",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: null,
                Inputs: new Dictionary<string, string> { ["FOO"] = "bar" },
                Variables: new Dictionary<string, string> { ["BAZ"] = "qux" }));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>();
        var run = ((PlatformResult<WorkflowRun>.Ok)result).Value;
        run.RunId.Should().Be("42");
        run.Status.Should().Be("pending");
        run.Conclusion.Should().BeNull("pipeline still running");
        run.RawMetadata.Should().NotBeNull();

        // Verify FOO + BAZ ended up as variables.
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("\"key\":\"FOO\"");
        capturedBody.Should().Contain("\"value\":\"bar\"");
        capturedBody.Should().Contain("\"key\":\"BAZ\"");
        capturedBody.Should().Contain("\"value\":\"qux\"");
    }

    [Test]
    public async Task DispatchWorkflowAsync_400_maps_to_InvalidRequest()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Post, "/pipeline", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
                "{\"message\":{\"ref\":[\"is invalid\"]}}"));

        var result = await client.DispatchWorkflowAsync(
            "g", "p",
            new WorkflowDispatchRequest(
                Ref: "bogus",
                WorkflowFileName: null,
                Inputs: new Dictionary<string, string>()));

        var failed = (PlatformResult<WorkflowRun>.Failed)result;
        failed.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("validation_failed");
    }

    [Test]
    public async Task GetRunStatusAsync_terminal_status_sets_Conclusion()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Get, "/pipelines/42", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":42,"status":"success","ref":"main","web_url":"https://gitlab.example.com/g/p/-/pipelines/42","created_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:05:00Z","source":"web"}"""));

        var result = await client.GetRunStatusAsync("g", "p", "42");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>();
        var run = ((PlatformResult<WorkflowRun>.Ok)result).Value;
        run.Status.Should().Be("success");
        run.Conclusion.Should().Be("success");
        run.CompletedAt.Should().NotBeNull();
    }

    [TestCase("pending", null)]
    [TestCase("running", null)]
    [TestCase("created", null)]
    [TestCase("success", "success")]
    [TestCase("failed", "failed")]
    [TestCase("canceled", "canceled")]
    [TestCase("skipped", "skipped")]
    public async Task GetRunStatusAsync_lifecycle_status_mapping(string status, string? expectedConclusion)
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Get, "/pipelines/1", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                $"{{\"id\":1,\"status\":\"{status}\",\"created_at\":\"2026-01-01T00:00:00Z\"}}"));

        var result = await client.GetRunStatusAsync("g", "p", "1");
        var run = ((PlatformResult<WorkflowRun>.Ok)result).Value;
        run.Status.Should().Be(status);
        run.Conclusion.Should().Be(expectedConclusion);
    }

    [Test]
    public async Task GetRunStatusAsync_404_maps_to_NotFound()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Get, "/pipelines/", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"404\"}"));

        var result = await client.GetRunStatusAsync("g", "p", "missing");
        ((PlatformResult<WorkflowRun>.Failed)result).Error
            .Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task ListRunJobsAsync_returns_jobs_with_artifact_metadata()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                [
                  {"id":100,"name":"build","status":"success","stage":"build","artifacts_file":{"filename":"out.zip","size":1024}},
                  {"id":101,"name":"test","status":"running","stage":"test"}
                ]
                """));

        var result = await client.ListRunJobsAsync("g", "p", "42");

        result.Should().BeOfType<PlatformResult<IReadOnlyList<WorkflowJob>>.Ok>();
        var jobs = ((PlatformResult<IReadOnlyList<WorkflowJob>>.Ok)result).Value;
        jobs.Should().HaveCount(2);
        jobs[0].JobId.Should().Be("100");
        jobs[0].Status.Should().Be("success");
        jobs[0].Conclusion.Should().Be("success");
        jobs[0].RawMetadata.Should().NotBeNull();
        jobs[1].Conclusion.Should().BeNull("running job has no conclusion");
        jobs[1].RawMetadata.Should().BeNull("no artifacts");
    }

    [Test]
    public async Task DownloadArtifactAsync_streams_bytes_within_cap()
    {
        var (client, handler) = TestFactory.BuildActions();
        var bytes = new byte[100];
        handler.AddRoute(HttpMethod.Get, "/jobs/100/artifacts", _ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            resp.Content.Headers.ContentLength = bytes.Length;
            return resp;
        });

        var result = await client.DownloadArtifactAsync("g", "p", "job:100");

        result.Should().BeOfType<PlatformResult<Stream>.Ok>();
        using var stream = ((PlatformResult<Stream>.Ok)result).Value;
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Length.Should().Be(100);
    }

    [Test]
    public async Task DownloadArtifactAsync_strips_job_prefix()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Get, "/jobs/55/artifacts", _ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[10]),
            };
            resp.Content.Headers.ContentLength = 10;
            return resp;
        });

        var result = await client.DownloadArtifactAsync("g", "p", "job:55");

        result.Should().BeOfType<PlatformResult<Stream>.Ok>();
        // Verify request hit the right URL (with stripped prefix).
        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].RequestUri.AbsolutePath.Should().EndWith("/jobs/55/artifacts");
    }

    [Test]
    public async Task DownloadArtifactAsync_rejects_oversize_via_content_length()
    {
        var (client, handler) = TestFactory.BuildActions(maxArtifactBytes: 100);
        handler.AddRoute(HttpMethod.Get, "/artifacts", _ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[200]),
            };
            resp.Content.Headers.ContentLength = 200;
            return resp;
        });

        var result = await client.DownloadArtifactAsync("g", "p", "job:9");

        var failed = (PlatformResult<Stream>.Failed)result;
        failed.Error.Should().BeOfType<PlatformError.InvalidRequest>()
            .Which.Code.Should().Be("artifact_too_large");
    }

    [Test]
    public async Task DownloadArtifactAsync_404_maps_to_NotFound()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Get, "/artifacts", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"404\"}"));

        var result = await client.DownloadArtifactAsync("g", "p", "job:9");

        ((PlatformResult<Stream>.Failed)result).Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task CancelRunAsync_success_returns_true()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Post, "/pipelines/42/cancel", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));

        var result = await client.CancelRunAsync("g", "p", "42");

        result.Should().BeOfType<PlatformResult<bool>.Ok>();
        ((PlatformResult<bool>.Ok)result).Value.Should().BeTrue();
    }

    [Test]
    public async Task CancelRunAsync_403_on_finished_returns_Ok_true()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Post, "/cancel", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                "{\"message\":\"already finished\"}"));

        var result = await client.CancelRunAsync("g", "p", "42");

        // GitLab returns 403 for already-finished pipelines; the
        // abstraction's contract says cancel-on-finished is a no-op
        // success. Driver translates.
        result.Should().BeOfType<PlatformResult<bool>.Ok>();
    }

    [Test]
    public async Task CancelRunAsync_500_maps_to_ServiceUnavailable()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Post, "/cancel", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"));

        var result = await client.CancelRunAsync("g", "p", "42");

        ((PlatformResult<bool>.Failed)result).Error
            .Should().BeOfType<PlatformError.ServiceUnavailable>();
    }

    [Test]
    public async Task DispatchWorkflowAsync_429_maps_to_RateLimited()
    {
        var (client, handler) = TestFactory.BuildActions();
        handler.AddRoute(HttpMethod.Post, "/pipeline", _ =>
        {
            var resp = FakeHttpMessageHandler.Json((HttpStatusCode)429,
                "{\"message\":\"rate limited\"}");
            resp.Headers.TryAddWithoutValidation("Retry-After", "60");
            return resp;
        });

        var result = await client.DispatchWorkflowAsync(
            "g", "p",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: null,
                Inputs: new Dictionary<string, string>()));

        var failed = (PlatformResult<WorkflowRun>.Failed)result;
        var rl = failed.Error.Should().BeOfType<PlatformError.RateLimited>().Subject;
        rl.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
    }
}
