using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

/// <summary>
/// Unit tests for the Gitea Actions surface. Covers all 5 methods on
/// <see cref="IGitPlatformActionsClient"/> + the 4 MB artifact cap
/// pattern preserved from <c>OctokitGitHubActionsClient</c>
/// (review-finding 6).
/// </summary>
[TestFixture]
public class GiteaActionsPlatformClientTests
{
    // ───────────── DispatchWorkflowAsync ─────────────

    [Test]
    public async Task DispatchWorkflowAsync_PostsThenReturnsLatestRun()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/o/r/actions/workflows/ci.yml/dispatches",
            HttpStatusCode.NoContent, "");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/o/r/actions/runs?event=workflow_dispatch",
            HttpStatusCode.OK,
            """
            {"total_count":1,"workflow_runs":[{
              "id":42,"status":"queued","conclusion":null,
              "html_url":"https://gitea.example.com/o/r/actions/runs/42",
              "started_at":"2026-04-21T00:00:00Z"}]}
            """);

        var result = await actions.DispatchWorkflowAsync("o", "r",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: "ci.yml",
                Inputs: new Dictionary<string, string> { ["foo"] = "bar" }));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>()
            .Which.Value.RunId.Should().Be("42");
    }

    [Test]
    public async Task DispatchWorkflowAsync_RejectsMissingWorkflowFile()
    {
        var (_, actions, _, _) = GiteaTestFixtures.Build();

        var result = await actions.DispatchWorkflowAsync("o", "r",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: null,
                Inputs: new Dictionary<string, string>()));

        var err = result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>()
            .Subject.Error.Should().BeOfType<PlatformError.InvalidRequest>().Subject;
        err.Code.Should().Be("missing_workflow_file_name");
    }

    // ───────────── GetRunStatusAsync ─────────────

    [Test]
    public async Task GetRunStatusAsync_MapsRunFields()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/o/r/actions/runs/42",
            HttpStatusCode.OK,
            """
            {"id":42,"status":"completed","conclusion":"success",
             "html_url":"https://x","started_at":"2026-04-21T00:00:00Z",
             "completed_at":"2026-04-21T00:05:00Z"}
            """);

        var result = await actions.GetRunStatusAsync("o", "r", "42");

        var run = result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>().Subject.Value;
        run.Status.Should().Be("completed");
        run.Conclusion.Should().Be("success");
        run.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task GetRunStatusAsync_MapsServerError()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/o/r/actions/runs/42",
            HttpStatusCode.BadGateway, "{}");

        var result = await actions.GetRunStatusAsync("o", "r", "42");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.ServiceUnavailable>();
    }

    // ───────────── ListRunJobsAsync ─────────────

    [Test]
    public async Task ListRunJobsAsync_ReturnsTypedJobs()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/o/r/actions/runs/42/jobs",
            HttpStatusCode.OK,
            """
            {"total_count":2,"jobs":[
              {"id":1,"name":"build","status":"completed","conclusion":"success"},
              {"id":2,"name":"test","status":"completed","conclusion":"failure"}
            ]}
            """);

        var result = await actions.ListRunJobsAsync("o", "r", "42");

        var jobs = result.Should()
            .BeOfType<PlatformResult<IReadOnlyList<WorkflowJob>>.Ok>().Subject.Value;
        jobs.Should().HaveCount(2);
        jobs[0].Name.Should().Be("build");
        jobs[1].Conclusion.Should().Be("failure");
    }

    // ───────────── DownloadArtifactAsync ─────────────

    [Test]
    public async Task DownloadArtifactAsync_ReturnsStream_OnSuccess()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        var payload = new byte[1024];
        new Random(1).NextBytes(payload);
        handler.Enqueue(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/o/r/actions/artifacts/7/zip",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });

        var result = await actions.DownloadArtifactAsync("o", "r", "7");

        result.Should().BeOfType<PlatformResult<Stream>.Ok>();
        using var stream = result.GetValueOrDefault()!;
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 2048));
        read.Should().Be(1024);
    }

    [Test]
    public void DownloadArtifactAsync_EnforcesDefaultFourMegabyteCap()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();

        // Synthesize a 5 MB body — bigger than the 4 MB default cap.
        const int sixtyFourK = 64 * 1024;
        const int totalBytes = 5 * 1024 * 1024;
        var chunk = new byte[sixtyFourK];
        var stream = new System.IO.MemoryStream();
        for (var i = 0; i < totalBytes / sixtyFourK; i++) stream.Write(chunk);
        stream.Position = 0;

        handler.Enqueue(HttpMethod.Get,
            "https://gitea.example.com/api/v1/repos/o/r/actions/artifacts/9/zip",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            });

        actions.MaxArtifactBytes.Should()
            .Be(GiteaActionsPlatformClient.DefaultMaxArtifactBytes,
                "the default cap is 4 MB per review-finding 6 from 31-3");

        Func<Task> act = async () =>
        {
            var result = await actions.DownloadArtifactAsync("o", "r", "9");
            using var s = result.GetValueOrDefault()!;
            var buf = new byte[8192];
            int read;
            long total = 0;
            while ((read = await s.ReadAsync(buf.AsMemory(0, buf.Length))) > 0)
            {
                total += read;
            }
        };

        act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(GiteaActionsPlatformClient.BoundedReadStream.TooLargeMessage);
    }

    [Test]
    public void DownloadArtifactAsync_RespectsConfiguredOverride()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:MaxArtifactBytes"] = "8388608", // 8 MB
            })
            .Build();
        var (_, actions, _, _) = GiteaTestFixtures.Build(configuration: config);

        actions.MaxArtifactBytes.Should().Be(8L * 1024 * 1024);
    }

    [Test]
    public void DownloadArtifactAsync_RevertsToDefault_OnZeroOrNegativeOverride()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:MaxArtifactBytes"] = "0",
            })
            .Build();
        var (_, actions, _, _) = GiteaTestFixtures.Build(configuration: config);

        actions.MaxArtifactBytes.Should()
            .Be(GiteaActionsPlatformClient.DefaultMaxArtifactBytes);
    }

    // ───────────── CancelRunAsync ─────────────

    [Test]
    public async Task CancelRunAsync_ReturnsTrueOnSuccess()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/o/r/actions/runs/42/cancel",
            HttpStatusCode.NoContent, "");

        var result = await actions.CancelRunAsync("o", "r", "42");

        result.Should().BeOfType<PlatformResult<bool>.Ok>().Which.Value.Should().BeTrue();
    }

    [Test]
    public async Task CancelRunAsync_MapsNotFound()
    {
        var (_, actions, handler, _) = GiteaTestFixtures.Build();
        handler.EnqueueJson(HttpMethod.Post,
            "https://gitea.example.com/api/v1/repos/o/r/actions/runs/42/cancel",
            HttpStatusCode.NotFound, "{}");

        var result = await actions.CancelRunAsync("o", "r", "42");

        result.Should().BeOfType<PlatformResult<bool>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.NotFound>();
    }
}
