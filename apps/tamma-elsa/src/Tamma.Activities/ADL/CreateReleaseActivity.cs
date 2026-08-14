using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ADL;

/// <summary>
/// Epic 38 follow-up #21 — creates a git-platform release (and its tag) for the
/// shipped version at the tail of the <c>deployment-pipeline</c> workflow (after a
/// successful production deploy). Replaces the prior <c>releaseStatus="deferred"</c>
/// placeholder with a real release cut through the MEDIATED integration seam.
///
/// <para><b>Mediation (TAMMA001):</b> the activity holds NO git credential and no
/// <c>IGitHubIntegrationService</c> / Octokit reference. The release is created via
/// <c>POST /api/v1/git/{owner}/{repo}/releases</c> through <see cref="TammaApiClient"/>,
/// where the per-tenant token is resolved and used server-side (the same seam the
/// PR / merge / branch ADL activities use). The tag / title / notes are composed
/// engine-side (pure, token-free).</para>
///
/// <para><b>Audit:</b> emits a <c>RELEASE.CREATED.SUCCESS</c> or
/// <c>RELEASE.CREATED.FAILED</c> DCB event into the workflow's <c>tamma:events</c>
/// transient list (drained durably to the tenant <c>domain_events</c> store) —
/// mirroring <see cref="EmitDeploymentEventActivity"/>. A failed release is NEVER
/// silently swallowed: the FAILED event is loud (error-status) and the error is
/// surfaced on the <see cref="ErrorCode"/> output.</para>
///
/// Outcomes:
///   - Created: the release was created (proceed to the pipeline-success terminal).
///   - Error:   the release create failed (the deploy still succeeded; the pipeline
///              records <c>releaseStatus=failed</c> and the loud FAILED event).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Create Release",
    "Create a git-platform release/tag for the shipped version (mediated, token-free)",
    Kind = ActivityKind.Task
)]
[FlowNode("Created", "Error")]
public class CreateReleaseActivity : Activity
{
    private readonly ILogger<CreateReleaseActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Release tag / version to create (e.g. deploy-a1b2c3d)")]
    public Input<string> TagName { get; set; } = default!;

    [Input(Description = "Target commit-ish (SHA or branch) the tag is cut from; empty = default branch")]
    public Input<string?> TargetRef { get; set; } = new((string?)null);

    [Input(Description = "Release title; empty = the tag name")]
    public Input<string?> ReleaseName { get; set; } = new((string?)null);

    [Input(Description = "Release notes / body (Markdown)")]
    public Input<string?> Body { get; set; } = new((string?)null);

    [Input(Description = "Create as a draft (unpublished) release")]
    public Input<bool> Draft { get; set; } = new(false);

    [Input(Description = "Mark the release as a pre-release")]
    public Input<bool> Prerelease { get; set; } = new(false);

    [Input(Description = "Issue number this deployment resolves (for the audit event)")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "Public URL of the created release")]
    public Output<string?> ReleaseUrl { get; set; } = default!;

    [Output(Description = "Numeric id of the created release")]
    public Output<string?> ReleaseId { get; set; } = default!;

    [Output(Description = "Tag the release points at")]
    public Output<string?> ReleaseTag { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> ErrorCode { get; set; } = default!;

    [JsonConstructor]
    public CreateReleaseActivity() { }

    /// <summary>
    /// Thin-client DI constructor. No <c>IGitHubIntegrationService</c> and no git
    /// token: the release create routes through
    /// <c>POST /api/v1/git/{owner}/{repo}/releases</c> via <see cref="TammaApiClient"/>.
    /// </summary>
    public CreateReleaseActivity(
        ILogger<CreateReleaseActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var tag = TagName.Get(context) ?? "";
        var targetRef = TargetRef.GetOrDefault(context);
        var name = ReleaseName.GetOrDefault(context);
        var body = Body.GetOrDefault(context);
        var draft = Draft.Get(context);
        var prerelease = Prerelease.Get(context);
        var issueNumber = IssueNumber.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));

        var request = new GitCreateReleaseRequest
        {
            TagName = tag,
            TargetRef = string.IsNullOrWhiteSpace(targetRef) ? null : targetRef,
            Name = string.IsNullOrWhiteSpace(name) ? tag : name,
            Body = body,
            Draft = draft,
            Prerelease = prerelease,
            IssueNumber = issueNumber,
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        _logger?.LogInformation(
            "Creating release {Tag} in {Repo} for issue #{Issue}", tag, repository, issueNumber);

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient.CreateReleaseAsync(repository, request, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        var outcome = MapResponse(response);
        if (outcome.Outcome == "Created")
        {
            ReleaseUrl.Set(context, outcome.ReleaseUrl);
            ReleaseId.Set(context, outcome.ReleaseId?.ToString());
            ReleaseTag.Set(context, outcome.ReleaseTag ?? tag);

            var evt = BuildReleaseEvent(
                success: true, issueNumber, repository, outcome.ReleaseTag ?? tag,
                outcome.ReleaseUrl, outcome.ReleaseId, tenantId, error: null);
            TammaEventEmitter.Emit(context, this, _logger, evt);

            _logger?.LogInformation(
                "Created release {Tag} in {Repo} ({Url})", outcome.ReleaseTag ?? tag, repository, outcome.ReleaseUrl);

            await context.CompleteActivityWithOutcomesAsync("Created");
        }
        else
        {
            ErrorCode.Set(context, outcome.ErrorCode ?? "release-creation-failed");

            var evt = BuildReleaseEvent(
                success: false, issueNumber, repository, tag,
                releaseUrl: null, releaseId: null, tenantId,
                error: outcome.FailureReason ?? outcome.ErrorCode ?? "release creation failed");
            TammaEventEmitter.Emit(context, this, _logger, evt);

            _logger?.LogWarning(
                "Failed to create release {Tag} in {Repo}: {Error}",
                tag, repository, outcome.FailureReason ?? outcome.ErrorCode);

            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    /// <summary>
    /// Project the git-mediation wire response into a typed outcome that maps
    /// directly to the activity's Elsa outcome (Created / Error). A null response
    /// (guard 403 / token 503 / auth 401 / transport) fails closed to Error — never
    /// a fabricated success. Pure — exposed for unit testing.
    /// </summary>
    public static ReleaseCreationOutcome MapResponse(GitCallResponse? response)
    {
        if (response is null)
            return ReleaseCreationOutcome.Failed("git-mediation-unavailable", "git mediation endpoint unavailable");

        if (response.Success && response.Outcome == "Created")
            return ReleaseCreationOutcome.Created(response.ReleaseId, response.ReleaseUrl, response.ReleaseTag);

        return ReleaseCreationOutcome.Failed(
            response.FailureCode ?? "release-creation-failed",
            response.FailureReason ?? "release creation failed");
    }

    /// <summary>
    /// Build the <c>RELEASE.CREATED.SUCCESS</c> / <c>RELEASE.CREATED.FAILED</c> DCB
    /// event. Tags carry the queryable DCB index keys (issue / repository / tag /
    /// tenant); Data carries the release payload (url / id) or the failure reason.
    /// Pure (no Elsa context) — exposed for unit testing.
    /// </summary>
    public static TammaEvent BuildReleaseEvent(
        bool success,
        int issueNumber,
        string repository,
        string tag,
        string? releaseUrl,
        long? releaseId,
        string? tenantId,
        string? error)
    {
        var tags = new Dictionary<string, object?>
        {
            ["issueId"] = issueNumber.ToString(),
            ["issueNumber"] = issueNumber.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(repository)) tags["repository"] = repository;
        if (!string.IsNullOrWhiteSpace(tag)) tags["tag"] = tag;
        if (!string.IsNullOrWhiteSpace(tenantId)) tags["tenantId"] = tenantId;

        var data = new Dictionary<string, object?> { ["tag"] = tag };
        if (success)
        {
            if (!string.IsNullOrWhiteSpace(releaseUrl)) data["releaseUrl"] = releaseUrl;
            if (releaseId is not null) data["releaseId"] = releaseId;
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            data["reason"] = error;
        }

        return new TammaEvent
        {
            EventType = success ? DeployEvents.ReleaseCreatedSuccess : DeployEvents.ReleaseCreatedFailed,
            Status = success ? "success" : "error",
            Error = success ? null : error,
            Tags = tags,
            Data = data,
        };
    }
}

/// <summary>
/// Typed result of <see cref="CreateReleaseActivity.MapResponse"/> — maps directly
/// to the activity's Elsa outcome (Created / Error).
/// </summary>
public sealed class ReleaseCreationOutcome
{
    public string Outcome { get; init; } = "Error";
    public long? ReleaseId { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleaseTag { get; init; }
    public string? ErrorCode { get; init; }
    public string? FailureReason { get; init; }

    public static ReleaseCreationOutcome Created(long? id, string? url, string? tag)
        => new() { Outcome = "Created", ReleaseId = id, ReleaseUrl = url, ReleaseTag = tag };

    public static ReleaseCreationOutcome Failed(string errorCode, string? reason = null)
        => new() { Outcome = "Error", ErrorCode = errorCode, FailureReason = reason };
}
