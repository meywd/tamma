using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Review.Models;

namespace Tamma.Activities.Review;

/// <summary>
/// Builds the structured <see cref="CodeReviewWorkflowResult"/> for a terminal path
/// (Story 7-1D AC2, completeness audit 2026-06-22 <c>CodeReview.md</c> §Missing #6) and
/// exposes it both as the typed <see cref="Result"/> output and as the workflow output
/// <c>result</c>. Replaces the three loose <c>SetOutput</c>s. Every terminal path
/// (merged / rejected / escalation-resolved / escalation-rejected / timeout /
/// validation-failed) maps to a <see cref="PRReviewStatus"/> via <see cref="FinalStatus"/>,
/// so a degraded outcome is never reported as a success.
/// </summary>
[Activity(
    "Tamma.Review",
    "Build Code Review Result",
    "Build the structured CodeReviewWorkflowResult and emit it as workflow output 'result'",
    Kind = ActivityKind.Task
)]
public class BuildCodeReviewResultActivity : CodeActivity<CodeReviewWorkflowResult>
{
    private readonly ILogger<BuildCodeReviewResultActivity>? _logger;

    [Input(Description = "Final review status")]
    public Input<PRReviewStatus> FinalStatus { get; set; } = new(PRReviewStatus.Pending);

    [Input(Description = "Whether the review ended in a merge (success)")]
    public Input<bool> Success { get; set; } = new(false);

    [Input(Description = "Pull request number (0 = none)")]
    public Input<int> PRNumber { get; set; } = new(0);

    [Input(Description = "Pull request URL")]
    public Input<string?> PRUrl { get; set; } = new((string?)null);

    [Input(Description = "Merge commit sha")]
    public Input<string?> MergeSha { get; set; } = new((string?)null);

    [Input(Description = "Total fix iterations")]
    public Input<int> TotalIterations { get; set; } = new(0);

    [Input(Description = "Whether the review was escalated to a senior")]
    public Input<bool> WasEscalated { get; set; } = new(false);

    [Input(Description = "Escalation resolution (e.g. resolved/rejected) when escalated")]
    public Input<string?> EscalationResolution { get; set; } = new((string?)null);

    [Input(Description = "Human-readable terminal message")]
    public Input<string?> Message { get; set; } = new((string?)null);

    [JsonConstructor]
    public BuildCodeReviewResultActivity() { }

    public BuildCodeReviewResultActivity(ILogger<BuildCodeReviewResultActivity> logger)
    {
        _logger = logger;
    }

    protected override void Execute(ActivityExecutionContext context)
    {
        var prNumber = PRNumber.Get(context);
        var totalIterations = TotalIterations.Get(context);

        var result = new CodeReviewWorkflowResult
        {
            Success = Success.Get(context),
            FinalStatus = FinalStatus.Get(context),
            PRNumber = prNumber > 0 ? prNumber : null,
            PRUrl = PRUrl.GetOrDefault(context),
            MergeSha = MergeSha.GetOrDefault(context),
            TotalIterations = totalIterations,
            ReviewRounds = totalIterations + 1,
            WasEscalated = WasEscalated.Get(context),
            EscalationResolution = EscalationResolution.GetOrDefault(context),
            Message = Message.GetOrDefault(context),
            CompletedAt = DateTime.UtcNow
        };

        context.SetResult(result);
        context.WorkflowExecutionContext.Output["result"] = result;

        _logger?.LogInformation(
            "Built code-review result: status={Status}, success={Success}, pr=#{Pr}, iterations={Iter}, escalated={Escalated}",
            result.FinalStatus, result.Success, prNumber, totalIterations, result.WasEscalated);
    }
}
