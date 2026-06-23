using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// IMPORTANT-2 — in-process resume seam for the <c>merge-approval</c> human gate.
///
/// <para>The gate (<c>WaitForMergeApprovalActivity</c>) suspends on the named
/// bookmark <c>adl-merge-approval-{issue}-{pr}</c> and, on resume, reads
/// <c>{decision, feedback, approver}</c> from the workflow input. Nothing drove
/// that bookmark before — wiring the gate into <c>single-issue-cycle</c> without a
/// producer left the merge step suspended forever. This endpoint closes the seam:
/// it looks the bookmark up by name, then runs the owning instance from that
/// bookmark with the decision payload INJECTED as workflow input (the mechanism
/// Elsa 3.5 documents — <c>IWorkflowClient.RunInstanceAsync</c> with
/// <c>RunWorkflowInstanceRequest.Input</c>, which lands in
/// <c>ActivityExecutionContext.WorkflowInput</c> that the gate reads).</para>
///
/// <para>This lives in the engine process because <c>IBookmarkStore</c> /
/// <c>IWorkflowRuntime</c> are in-process here. <c>Tamma.Api</c> exposes the
/// RBAC-gated public surface (<c>POST /api/adl/{instanceId}/merge-approval</c>)
/// and forwards to this engine endpoint — the same API→engine hop every other
/// engine call uses.</para>
/// </summary>
public static class MergeApprovalResumeEndpoint
{
    public sealed record ResumeRequest(
        int IssueNumber,
        int PrNumber,
        string Decision,
        string? Feedback,
        string? Approver);

    /// <summary>Bookmark name the gate registers — must match
    /// <c>WaitForMergeApprovalActivity.RunAsync</c>.</summary>
    public static string BookmarkName(int issueNumber, int prNumber)
        => $"adl-merge-approval-{issueNumber}-{prNumber}";

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.MergeApprovalResume");

        if (string.IsNullOrWhiteSpace(request.Decision))
            return Results.BadRequest(new { error = "decision is required" });

        var name = BookmarkName(request.IssueNumber, request.PrNumber);

        // Find the suspended gate bookmark by name. There is at most one live
        // gate per (issue, pr); if a stale duplicate exists we resume the first.
        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended merge-approval bookmark {Bookmark} — gate not waiting (already decided, or wrong issue/pr)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No merge-approval gate is currently waiting for this issue/PR.",
            });
        }

        var bookmark = bookmarks[0];

        // Inject the decision payload as workflow input — the gate's resume
        // callback reads context.WorkflowInput["decision"|"feedback"|"approver"].
        var input = new Dictionary<string, object>
        {
            ["decision"] = request.Decision,
        };
        if (request.Feedback is not null) input["feedback"] = request.Feedback;
        if (request.Approver is not null) input["approver"] = request.Approver;

        var client = await workflowRuntime
            .CreateClientAsync(bookmark.WorkflowInstanceId, ct)
            .ConfigureAwait(false);

        await client.RunInstanceAsync(
            new RunWorkflowInstanceRequest
            {
                BookmarkId = bookmark.Id,
                Input = input,
            },
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "Resumed merge-approval gate {Bookmark} (instance {InstanceId}) with decision {Decision}",
            name, bookmark.WorkflowInstanceId, request.Decision);

        return Results.Ok(new
        {
            resumed = true,
            bookmark = name,
            workflowInstanceId = bookmark.WorkflowInstanceId,
            decision = request.Decision,
        });
    }
}
