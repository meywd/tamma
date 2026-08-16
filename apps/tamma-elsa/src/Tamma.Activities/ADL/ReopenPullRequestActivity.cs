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
/// Story 31-13 — reopens a closed pull request through the mediated, governed git
/// plane (<c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/reopen</c>). The
/// inverse of <see cref="ClosePullRequestActivity"/>; same thin
/// <see cref="TammaApiClient"/> shape (no token, no vendor service).
///
/// <para>Emits the headline <c>GIT.PR_REOPENED.SUCCESS</c> /
/// <c>GIT.PR_REOPENED.FAILED</c> DCB event. NEVER throws; a null response fails
/// closed to the <c>Error</c> outcome.</para>
///
/// Outcomes:
///   - Reopened: the PR was reopened (state == "open").
///   - Error:    the reopen did not happen (routed to the workflow's failure edge).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Reopen Pull Request",
    "Reopen a closed pull request through the mediated git plane",
    Kind = ActivityKind.Task
)]
[FlowNode("Reopened", "Error")]
public class ReopenPullRequestActivity : Activity
{
    /// <summary>Headline DCB event type on the reopen success path.</summary>
    public const string SuccessEventType = "GIT.PR_REOPENED.SUCCESS";

    /// <summary>Headline DCB event type on the reopen failure path.</summary>
    public const string FailedEventType = "GIT.PR_REOPENED.FAILED";

    private readonly ILogger<ReopenPullRequestActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "The PR state after the reopen (\"open\" on success)")]
    public Output<string?> PrState { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> FailureCode { get; set; } = default!;

    [Output(Description = "Human-readable failure reason when the Error outcome fires")]
    public Output<string?> Error { get; set; } = default!;

    [JsonConstructor]
    public ReopenPullRequestActivity() { }

    public ReopenPullRequestActivity(
        ILogger<ReopenPullRequestActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var prNumber = PrNumber.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));

        var request = new GitReopenPrRequest
        {
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient.ReopenPullRequestAsync(repository, prNumber, request, tenantId, context.CancellationToken)
            .ConfigureAwait(false);

        var outcome = MapResponse(response);
        if (outcome.Success)
        {
            PrState.Set(context, outcome.PrState);
            TammaEventEmitter.Emit(context, this, _logger, new TammaEvent
            {
                EventType = SuccessEventType,
                Status = "success",
                Data = new Dictionary<string, object?>
                {
                    ["repository"] = repository,
                    ["prNumber"] = prNumber,
                    ["prState"] = outcome.PrState,
                },
            });
            await context.CompleteActivityWithOutcomesAsync("Reopened");
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
    /// Project the git-mediation wire response into a typed reopen outcome. A null
    /// response (guard / token / auth / transport / governance 409) fails closed.
    /// </summary>
    public static PrLifecycleOutcome MapResponse(GitCallResponse? response)
    {
        if (response is null)
            return PrLifecycleOutcome.Failed("git-mediation-unavailable", "git mediation endpoint unavailable");

        if (response.Success)
            return PrLifecycleOutcome.Ok(response.PrState);

        return PrLifecycleOutcome.Failed(
            response.FailureCode ?? "unknown",
            response.FailureReason ?? "reopen pull request failed");
    }
}

/// <summary>
/// Typed result of a PR close/reopen mediation call — maps to the activity's Elsa
/// outcome (Closed/Reopened vs Error). On failure <see cref="PrState"/> is null so
/// a consumer can never read a false state.
/// </summary>
public sealed class PrLifecycleOutcome
{
    public bool Success { get; init; }
    public string? PrState { get; init; }
    public string? FailureCode { get; init; }
    public string? Error { get; init; }

    public static PrLifecycleOutcome Ok(string? prState)
        => new() { Success = true, PrState = prState };

    public static PrLifecycleOutcome Failed(string failureCode, string error)
        => new() { Success = false, FailureCode = failureCode, Error = error };
}
