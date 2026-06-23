using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services;
using Tamma.Core.Logging;

namespace Tamma.Api.Endpoints;

/// <summary>
/// IMPORTANT-2 — RBAC-gated public surface that lets a human DRIVE the
/// <c>merge-approval</c> gate of the autonomous loop.
///
/// <para>The gate (<c>WaitForMergeApprovalActivity</c>) suspends the
/// <c>single-issue-cycle</c> on the named bookmark
/// <c>adl-merge-approval-{issue}-{pr}</c> and reads
/// <c>{decision, feedback, approver}</c> from the workflow input on resume.
/// Without a producer, wiring the gate into the cycle would suspend the merge
/// step forever. This endpoint is that producer: it forwards to the engine's
/// in-process resume seam (<c>POST /elsa/api/adl/merge-approval/resume</c>),
/// which looks the bookmark up and runs the owning instance with the decision
/// payload injected.</para>
///
/// <para><b>Authorization</b>: <c>WorkflowsManage</c> (permission
/// <c>workflows:manage</c> → tenant owner/admin). Member-role SaaS callers hit
/// 403 at the policy — driving a merge decision is a workflow-management
/// action, mirroring the engine command/issue-mutation routes.</para>
/// </summary>
public static class AdlEndpoints
{
    /// <summary>Accepted decisions (case-insensitive). Anything else routes to
    /// the gate's <c>Invalid</c> outcome — but we reject obviously empty input
    /// up front so the caller gets a 400 instead of silently escalating.</summary>
    public sealed record MergeApprovalDecisionRequest(
        int IssueNumber,
        int PrNumber,
        string Decision,
        string? Feedback,
        string? Approver);

    /// <summary>
    /// Resume the merge-approval gate for an issue/PR with a human decision.
    /// Route: <c>POST /api/adl/merge-approval/resume</c>.
    /// </summary>
    public static async Task<IResult> ResumeMergeApproval(
        [FromBody] MergeApprovalDecisionRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.AdlEndpoints");

        if (string.IsNullOrWhiteSpace(req.Decision))
            return Results.BadRequest(new { error = "decision is required (merge|test|reject)" });
        if (req.IssueNumber <= 0 || req.PrNumber <= 0)
            return Results.BadRequest(new { error = "issueNumber and prNumber are required (> 0)" });

        try
        {
            var result = await elsa.ResumeMergeApprovalAsync(
                req.IssueNumber, req.PrNumber, req.Decision, req.Feedback, req.Approver);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No merge-approval gate is currently suspended for this issue/PR.",
                });
            }

            logger.LogInformation(
                "Drove merge-approval gate for issue #{Issue} PR #{Pr} with decision {Decision}",
                req.IssueNumber, req.PrNumber, LogSanitizer.Clean(req.Decision));

            return Results.Ok(new
            {
                resumed = result.Resumed,
                workflowInstanceId = result.WorkflowInstanceId,
                decision = req.Decision,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resume merge-approval gate for issue #{Issue} PR #{Pr}",
                req.IssueNumber, req.PrNumber);
            return Results.Problem(
                detail: "Failed to resume the merge-approval gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
