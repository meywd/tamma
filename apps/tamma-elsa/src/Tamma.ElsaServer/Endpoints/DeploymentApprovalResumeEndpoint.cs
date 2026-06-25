using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// In-process resume seam for the <c>deployment-pipeline</c> production-approval
/// human gate (completeness audit P0 item 3). Mirrors
/// <see cref="MergeApprovalResumeEndpoint"/>.
///
/// <para>The gate (<c>WaitForDeploymentApprovalActivity</c>) suspends on the named
/// bookmark <c>adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{mergeSha}</c>
/// (tenant + repo + SHA folded in) and, on resume, reads
/// <c>{decision, feedback, approver}</c> from the workflow input. This endpoint
/// looks the bookmark up by name, then runs the owning instance from that bookmark
/// with the decision payload INJECTED as workflow input
/// (<c>IWorkflowClient.RunInstanceAsync</c> with <c>RunWorkflowInstanceRequest.Input</c>,
/// which lands in <c>ActivityExecutionContext.WorkflowInput</c> the gate reads).</para>
///
/// <para>This lives in the engine process because <c>IBookmarkStore</c> /
/// <c>IWorkflowRuntime</c> are in-process here. <c>Tamma.Api</c> exposes the
/// RBAC-gated public surface and forwards to this engine endpoint.</para>
///
/// <para><b>SECURITY:</b>
/// <list type="bullet">
///   <item><description><b>Tenant scoping</b> — the bookmark name carries the
///   tenant id + repository that the (tenant-scoped) <c>Tamma.Api</c> caller
///   supplies. A caller scoped to tenant A computes a name with tenant A, so it
///   can NEVER resolve tenant B's gate: a cross-tenant attempt simply 404s.</description></item>
///   <item><description><b>Collision refusal</b> — if more than one bookmark
///   matches the (globally-unique) name, we REFUSE with 409 rather than resume an
///   arbitrary one.</description></item>
///   <item><description><b>Engine-service-only</b> — the route is mapped with
///   <c>.RequireAuthorization()</c> in <c>Program.cs</c> so only the
///   Tamma.Api→engine hop (presenting the Elsa admin API key) reaches it;
///   anonymous public callers get 401.</description></item>
/// </list></para>
/// </summary>
public static class DeploymentApprovalResumeEndpoint
{
    public sealed record ResumeRequest(
        int IssueNumber,
        string Decision,
        string? Feedback,
        string? Approver,
        // Tenant + repository + mergeSha scope the bookmark lookup. Supplied by the
        // tenant-scoped Tamma.Api caller; folded into the bookmark name so the
        // lookup can only ever match a gate in the caller's own tenant/repo/SHA.
        string? TenantId,
        string? Repository,
        string? MergeSha);

    /// <summary>Bookmark name the gate registers — delegates to the SINGLE
    /// canonical builder shared with <see cref="WaitForDeploymentApprovalActivity"/>
    /// so suspend-side and resume-side names match byte-for-byte.</summary>
    public static string BookmarkName(string? tenantId, string? repository, int issueNumber, string? mergeSha)
        => WaitForDeploymentApprovalActivity.BookmarkName(tenantId, repository, issueNumber, mergeSha);

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.DeploymentApprovalResume");

        if (string.IsNullOrWhiteSpace(request.Decision))
            return Results.BadRequest(new { error = "decision is required" });

        var name = BookmarkName(request.TenantId, request.Repository, request.IssueNumber, request.MergeSha);

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended deploy-approval bookmark {Bookmark} — gate not waiting (already decided, wrong issue/sha, or not this tenant/repo)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No production-deploy approval gate is currently waiting for this issue/SHA.",
            });
        }

        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous deploy-approval bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                name, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = name,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this gate; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

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
            "Resumed deploy-approval gate {Bookmark} (instance {InstanceId}) with decision {Decision}",
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
