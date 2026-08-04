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
/// Story 31-13 — closes a pull request through the mediated, governed git plane
/// (<c>POST /api/v1/git/{owner}/{repo}/pull-requests/{n}/close</c>). Thin
/// <see cref="TammaApiClient"/> wrapper (the <c>DeleteBranch</c> shape): it holds
/// no git token and no vendor service; the per-tenant credential is resolved +
/// used server-side. Reversible via <see cref="ReopenPullRequestActivity"/>.
///
/// <para>Emits the headline <c>GIT.PR_CLOSED.SUCCESS</c> / <c>GIT.PR_CLOSED.FAILED</c>
/// DCB event onto the workflow event stream. NEVER throws and NEVER reports a false
/// success — a null response (guard 403 / token 503 / auth 401 / transport / a
/// governance 409) fails closed to the <c>Error</c> outcome.</para>
///
/// Outcomes:
///   - Closed: the PR was closed (state == "closed").
///   - Error:  the close did not happen (routed to the workflow's failure edge).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Close Pull Request",
    "Close a pull request through the mediated git plane (reversible via reopen)",
    Kind = ActivityKind.Task
)]
[FlowNode("Closed", "Error")]
public class ClosePullRequestActivity : Activity
{
    /// <summary>Headline DCB event type on the close success/failure path.</summary>
    public const string SuccessEventType = "GIT.PR_CLOSED.SUCCESS";

    /// <summary>Headline DCB event type on the close failure path.</summary>
    public const string FailedEventType = "GIT.PR_CLOSED.FAILED";

    private readonly ILogger<ClosePullRequestActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Pull request number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for BYOK token resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "The PR state after the close (\"closed\" on success)")]
    public Output<string?> PrState { get; set; } = default!;

    [Output(Description = "Failure classification when the Error outcome fires")]
    public Output<string?> FailureCode { get; set; } = default!;

    [Output(Description = "Human-readable failure reason when the Error outcome fires")]
    public Output<string?> Error { get; set; } = default!;

    [JsonConstructor]
    public ClosePullRequestActivity() { }

    public ClosePullRequestActivity(
        ILogger<ClosePullRequestActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var prNumber = PrNumber.Get(context);
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.Get(context));

        var request = new GitClosePrRequest
        {
            CorrelationId = context.WorkflowExecutionContext.Id,
        };

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        var response = await apiClient.ClosePullRequestAsync(repository, prNumber, request, tenantId, context.CancellationToken)
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
            await context.CompleteActivityWithOutcomesAsync("Closed");
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
    /// Project the git-mediation wire response into a typed close outcome. A null
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
            response.FailureReason ?? "close pull request failed");
    }
}
