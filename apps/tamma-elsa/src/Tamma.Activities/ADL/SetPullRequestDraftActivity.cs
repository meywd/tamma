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
/// Story 31-13 — toggle a pull request's draft state through the governed
/// git-mediation plane (<c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/draft</c>,
/// GraphQL-backed on GitHub because REST cannot do it).
///
/// <para><b>Why the autonomous loop needs this.</b> The loop opens its PR as a DRAFT
/// (<c>SingleIssueCycleWorkflow</c> passes <c>draft = true</c>) and GitHub refuses to
/// merge a draft PR. Until this activity existed nothing ever flipped it back, so the
/// cycle would pass CI, ask a human to approve the merge, and then attempt to merge a
/// PR that <i>cannot</i> merge. Marking the PR ready before the merge gate is what
/// lets a cycle actually complete.</para>
///
/// <para>Mirrors <see cref="ClosePullRequestActivity"/>: a thin
/// <see cref="TammaApiClient"/> wrapper, one headline DCB event per terminal, and a
/// typed <c>Error</c> outcome instead of a throw — a failure here must be routable to
/// escalation, never silently treated as "ready".</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Set PR Draft State",
    "Marks a pull request ready-for-review (or back to draft)",
    Kind = ActivityKind.Task
)]
[FlowNode("DraftSet", "Error")]
public class SetPullRequestDraftActivity : Activity
{
    /// <summary>Headline DCB event type on the draft-set success path.</summary>
    public const string SuccessEventType = "GIT.PR_DRAFT_SET.SUCCESS";

    /// <summary>Headline DCB event type on the draft-set failure path.</summary>
    public const string FailedEventType = "GIT.PR_DRAFT_SET.FAILED";

    private readonly ILogger<SetPullRequestDraftActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    /// <summary>
    /// <c>false</c> (the default) marks the PR READY FOR REVIEW — the direction the
    /// autonomous loop needs before its merge gate. <c>true</c> converts back to draft.
    /// </summary>
    [Input(Description = "Target draft state. false = ready for review (the merge-gate direction)")]
    public Input<bool> Draft { get; set; } = new(false);

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "The PR's draft state after the call")]
    public Output<bool?> IsDraft { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> FailureCode { get; set; } = default!;

    [Output(Description = "Human-readable failure reason when the Error outcome fires")]
    public Output<string?> Error { get; set; } = default!;

    [JsonConstructor]
    public SetPullRequestDraftActivity() { }

    public SetPullRequestDraftActivity(
        ILogger<SetPullRequestDraftActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var prNumber = PrNumber.Get(context);
        var draft = Draft.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));

        var request = new GitPrDraftRequest
        {
            Draft = draft,
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient
            .SetPullRequestDraftAsync(repository, prNumber, request, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        var outcome = MapResponse(response);
        if (outcome.Success)
        {
            IsDraft.Set(context, outcome.IsDraft);
            TammaEventEmitter.Emit(context, this, _logger, new TammaEvent
            {
                EventType = SuccessEventType,
                Status = "success",
                Data = new Dictionary<string, object?>
                {
                    ["repository"] = repository,
                    ["prNumber"] = prNumber,
                    ["isDraft"] = outcome.IsDraft,
                },
            });
            await context.CompleteActivityWithOutcomesAsync("DraftSet");
        }
        else
        {
            FailureCode.Set(context, outcome.FailureCode);
            Error.Set(context, outcome.Error);
            TammaEventEmitter.Emit(context, this, _logger, new TammaEvent
            {
                EventType = FailedEventType,
                Status = "error",
                Error = outcome.Error,
                Data = new Dictionary<string, object?>
                {
                    ["repository"] = repository,
                    ["prNumber"] = prNumber,
                    ["failureCode"] = outcome.FailureCode,
                },
            });
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    /// <summary>
    /// Project the git-mediation wire response into a typed draft outcome. A null
    /// response (guard / token / auth / transport / governance 409) fails closed —
    /// the caller must never read "ready" from an unanswered call.
    /// </summary>
    public static PrDraftOutcome MapResponse(GitCallResponse? response)
    {
        if (response is null)
            return PrDraftOutcome.Failed("git-mediation-unavailable", "git mediation endpoint unavailable");

        if (response.Success)
            return PrDraftOutcome.Ok(response.IsDraft);

        return PrDraftOutcome.Failed(
            response.FailureCode ?? "unknown",
            response.FailureReason ?? "set pull request draft state failed");
    }
}

/// <summary>
/// Typed result of a draft-state mediation call. On failure <see cref="IsDraft"/> is
/// null so a consumer can never read a false "ready for review".
/// </summary>
public sealed class PrDraftOutcome
{
    public bool Success { get; init; }
    public bool? IsDraft { get; init; }
    public string? FailureCode { get; init; }
    public string? Error { get; init; }

    public static PrDraftOutcome Ok(bool? isDraft) => new() { Success = true, IsDraft = isDraft };

    public static PrDraftOutcome Failed(string failureCode, string error)
        => new() { Success = false, FailureCode = failureCode, Error = error };
}
