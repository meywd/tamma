using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// IMPORTANT-2 — in-process resume seam for the <c>merge-approval</c> human gate.
///
/// <para>The gate (<c>WaitForMergeApprovalActivity</c>) suspends on the named
/// bookmark <c>adl-merge-approval-{tenant}-{repo}-{issue}-{pr}</c> (SECURITY C2 —
/// tenant + repo folded in) and, on resume, reads
/// <c>{decision, feedback, approver}</c> from the workflow input. This endpoint
/// looks the bookmark up by name, then runs the owning instance from that
/// bookmark with the decision payload INJECTED as workflow input (the mechanism
/// Elsa 3.5 documents — <c>IWorkflowClient.RunInstanceAsync</c> with
/// <c>RunWorkflowInstanceRequest.Input</c>, which lands in
/// <c>ActivityExecutionContext.WorkflowInput</c> that the gate reads).</para>
///
/// <para>This lives in the engine process because <c>IBookmarkStore</c> /
/// <c>IWorkflowRuntime</c> are in-process here. <c>Tamma.Api</c> exposes the
/// RBAC-gated public surface and forwards to this engine endpoint.</para>
///
/// <para><b>SECURITY:</b>
/// <list type="bullet">
///   <item><description><b>C1/C2 (tenant scoping)</b> — the bookmark name carries
///   the tenant id + repository that the (tenant-scoped) <c>Tamma.Api</c> caller
///   supplies. A caller scoped to tenant A computes a name with tenant A, so it
///   can NEVER resolve tenant B's gate: a cross-tenant attempt simply 404s
///   (bookmark not found), it never acts.</description></item>
///   <item><description><b>C2 (collision refusal)</b> — if more than one
///   bookmark matches the (now globally-unique) name, we REFUSE with 409 rather
///   than resume an arbitrary <c>bookmarks[0]</c>.</description></item>
///   <item><description><b>C3 (engine-service-only)</b> — the route is mapped
///   with <c>.RequireAuthorization()</c> in <c>Program.cs</c> so only an
///   authenticated caller (the Tamma.Api→engine hop presenting the Elsa admin
///   API key) reaches it; anonymous public-internet callers get 401. It is also
///   excluded from the public nginx <c>/elsa/api/</c> route (internal-hop-only).
///   </description></item>
/// </list></para>
/// </summary>
public static class MergeApprovalResumeEndpoint
{
    public sealed record ResumeRequest(
        int IssueNumber,
        int PrNumber,
        string Decision,
        string? Feedback,
        string? Approver,
        // SECURITY C1/C2 — tenant + repository scope the bookmark lookup. Supplied
        // by the tenant-scoped Tamma.Api caller; folded into the bookmark name so
        // the lookup can only ever match a gate in the caller's own tenant/repo.
        string? TenantId,
        string? Repository);

    /// <summary>Bookmark name the gate registers — delegates to the SINGLE
    /// canonical builder shared with <see cref="WaitForMergeApprovalActivity"/> so
    /// suspend-side and resume-side names match byte-for-byte (SECURITY C2).</summary>
    public static string BookmarkName(string? tenantId, string? repository, int issueNumber, int prNumber)
        => WaitForMergeApprovalActivity.BookmarkName(tenantId, repository, issueNumber, prNumber);

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

        // SECURITY C1/C2 — the name carries the caller's tenant + repo. A caller
        // in tenant A produces a name keyed by tenant A and therefore can only
        // ever resolve tenant A's gate.
        var name = BookmarkName(request.TenantId, request.Repository, request.IssueNumber, request.PrNumber);

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended merge-approval bookmark {Bookmark} — gate not waiting (already decided, wrong issue/pr, or not this tenant/repo)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No merge-approval gate is currently waiting for this issue/PR.",
            });
        }

        // SECURITY C2 — the name is globally unique (tenant + repo + issue + pr),
        // so >1 match is an integrity violation, not a "pick the first" situation.
        // REFUSE rather than resume an arbitrary instance.
        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous merge-approval bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
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
