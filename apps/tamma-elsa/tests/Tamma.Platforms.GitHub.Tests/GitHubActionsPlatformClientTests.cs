using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Story 31-3 — translation tests for
/// <see cref="GitHubActionsPlatformClient"/>: each
/// <see cref="IGitHubActionsClient"/> result shape (NotConfigured /
/// success / 4xx / 5xx / null) maps to the correct
/// <see cref="PlatformResult{T}"/> +
/// <see cref="PlatformError"/> variant.
/// </summary>
[TestFixture]
public sealed class GitHubActionsPlatformClientTests
{
    private static GitHubActionsPlatformClient BuildClient(IGitHubActionsClient inner) =>
        new(inner);

    [Test]
    public void Constructor_rejects_null_inner_client()
    {
        Action act = () => new GitHubActionsPlatformClient(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── DispatchWorkflowAsync ──────────────────────────────────────

    [Test]
    public async Task DispatchWorkflowAsync_returns_invalid_request_when_workflow_file_missing()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = BuildClient(inner);

        var result = await client.DispatchWorkflowAsync(
            "acme", "repo",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: null,
                Inputs: new Dictionary<string, string>()));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>();
        var failed = (PlatformResult<WorkflowRun>.Failed)result;
        failed.Error.Should().BeOfType<PlatformError.InvalidRequest>();
        ((PlatformError.InvalidRequest)failed.Error).Code.Should().Be("workflow_file_required");
    }

    [Test]
    public async Task DispatchWorkflowAsync_returns_service_unavailable_when_inner_not_configured()
    {
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.DispatchWorkflowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchApiResult(0, "github_client_not_configured", NotConfigured: true));

        var client = BuildClient(inner.Object);

        var result = await client.DispatchWorkflowAsync(
            "acme", "repo",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: "tamma-agent.yml",
                Inputs: new Dictionary<string, string>()));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.ServiceUnavailable>();
    }

    [Test]
    public async Task DispatchWorkflowAsync_returns_ok_with_placeholder_run_on_204()
    {
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.DispatchWorkflowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchApiResult(204, null));

        var client = BuildClient(inner.Object);

        var result = await client.DispatchWorkflowAsync(
            "acme", "repo",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: "tamma-agent.yml",
                Inputs: new Dictionary<string, string>()));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>();
        ((PlatformResult<WorkflowRun>.Ok)result).Value.Status.Should().Be("queued");
    }

    [TestCase(401, typeof(PlatformError.AuthExpired))]
    [TestCase(403, typeof(PlatformError.PermissionDenied))]
    [TestCase(404, typeof(PlatformError.NotFound))]
    [TestCase(429, typeof(PlatformError.RateLimited))]
    [TestCase(500, typeof(PlatformError.ServiceUnavailable))]
    [TestCase(503, typeof(PlatformError.ServiceUnavailable))]
    [TestCase(422, typeof(PlatformError.InvalidRequest))]
    public async Task DispatchWorkflowAsync_translates_status_to_error(int status, Type expectedErrorType)
    {
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.DispatchWorkflowAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchApiResult(status, "boom"));

        var client = BuildClient(inner.Object);

        var result = await client.DispatchWorkflowAsync(
            "acme", "repo",
            new WorkflowDispatchRequest(
                Ref: "main",
                WorkflowFileName: "tamma-agent.yml",
                Inputs: new Dictionary<string, string>()));

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>();
        var err = ((PlatformResult<WorkflowRun>.Failed)result).Error;
        err.Should().BeOfType(expectedErrorType);
    }

    // ── GetRunStatusAsync ──────────────────────────────────────────

    [Test]
    public async Task GetRunStatusAsync_returns_invalid_request_for_non_numeric_run_id()
    {
        var inner = Mock.Of<IGitHubActionsClient>();
        var client = BuildClient(inner);

        var result = await client.GetRunStatusAsync("acme", "repo", "not-a-number");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>();
        var err = ((PlatformResult<WorkflowRun>.Failed)result).Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>();
        ((PlatformError.InvalidRequest)err).Code.Should().Be("invalid_run_id");
    }

    [Test]
    public async Task GetRunStatusAsync_returns_not_found_when_inner_returns_null()
    {
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.GetWorkflowRunAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowRunSummary?)null);

        var client = BuildClient(inner.Object);

        var result = await client.GetRunStatusAsync("acme", "repo", "12345");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Failed>();
        ((PlatformResult<WorkflowRun>.Failed)result).Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task GetRunStatusAsync_returns_ok_with_translated_run()
    {
        var summary = new WorkflowRunSummary(
            Id: 12345L,
            Status: "completed",
            Conclusion: "success",
            HtmlUrl: "https://github.com/acme/repo/actions/runs/12345",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            HeadBranch: "main",
            Event: "workflow_dispatch",
            ArtifactsUrl: "");
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.GetWorkflowRunAsync(
                "acme", "repo", 12345L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var client = BuildClient(inner.Object);

        var result = await client.GetRunStatusAsync("acme", "repo", "12345");

        result.Should().BeOfType<PlatformResult<WorkflowRun>.Ok>();
        var run = ((PlatformResult<WorkflowRun>.Ok)result).Value;
        run.RunId.Should().Be("12345");
        run.Status.Should().Be("completed");
        run.Conclusion.Should().Be("success");
    }

    // ── ListRunJobsAsync / CancelRunAsync — explicit ServiceUnavailable ─

    [Test]
    public async Task ListRunJobsAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.ListRunJobsAsync("acme", "repo", "12345");
        result.Should().BeOfType<PlatformResult<IReadOnlyList<WorkflowJob>>.ServiceUnavailable>();
    }

    [Test]
    public async Task CancelRunAsync_returns_service_unavailable()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.CancelRunAsync("acme", "repo", "12345");
        result.Should().BeOfType<PlatformResult<bool>.ServiceUnavailable>();
    }

    // ── DownloadArtifactAsync ──────────────────────────────────────

    [Test]
    public async Task DownloadArtifactAsync_returns_invalid_request_for_non_numeric_id()
    {
        var client = BuildClient(Mock.Of<IGitHubActionsClient>());
        var result = await client.DownloadArtifactAsync("acme", "repo", "abc");
        result.Should().BeOfType<PlatformResult<Stream>.Failed>();
        var err = ((PlatformResult<Stream>.Failed)result).Error;
        err.Should().BeOfType<PlatformError.InvalidRequest>();
        ((PlatformError.InvalidRequest)err).Code.Should().Be("invalid_artifact_id");
    }

    [Test]
    public async Task DownloadArtifactAsync_returns_not_found_when_inner_returns_null()
    {
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.DownloadArtifactZipAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var client = BuildClient(inner.Object);

        var result = await client.DownloadArtifactAsync("acme", "repo", "9876");

        result.Should().BeOfType<PlatformResult<Stream>.Failed>();
        ((PlatformResult<Stream>.Failed)result).Error.Should().BeOfType<PlatformError.NotFound>();
    }

    [Test]
    public async Task DownloadArtifactAsync_returns_ok_stream_on_success()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var inner = new Mock<IGitHubActionsClient>();
        inner.Setup(x => x.DownloadArtifactZipAsync(
                "acme", "repo", 9876L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);

        var client = BuildClient(inner.Object);

        var result = await client.DownloadArtifactAsync("acme", "repo", "9876");

        result.Should().BeOfType<PlatformResult<Stream>.Ok>();
        using var stream = ((PlatformResult<Stream>.Ok)result).Value;
        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);
        reader.ToArray().Should().Equal(bytes);
    }
}
