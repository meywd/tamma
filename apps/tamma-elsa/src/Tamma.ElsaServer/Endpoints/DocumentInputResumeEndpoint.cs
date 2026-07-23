using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Documents;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Story 39-13 (D3) — the generic engine-side resume seam for the ONE domain-input gate
/// (<see cref="WaitForDocumentInputActivity"/>). Mirrors
/// <see cref="DocumentDecisionResumeEndpoint"/> / <see cref="DesignResumeEndpoint"/>
/// byte-for-byte in security posture. The legacy <c>ClarifyResumeEndpoint</c> becomes a thin
/// adapter forwarding onto this generic path.
///
/// <para>The gate suspends on the tenant-folded bookmark
/// <c>document-input-{tenant}-{session}</c> and, on resume, reads
/// <c>{Received, InputJson}</c> from the workflow input. This endpoint looks the bookmark up
/// by name, then runs the owning instance from that bookmark with the input payload INJECTED
/// as workflow input.</para>
///
/// <para><b>SECURITY</b> — the bookmark name carries the caller's tenant + session (No IDOR),
/// a &gt;1 match REFUSES with 409, and the route is mapped with <c>.RequireAuthorization()</c>
/// in <c>Program.cs</c> (engine-service-only). Same posture as the sibling gates.</para>
/// </summary>
public static class DocumentInputResumeEndpoint
{
    public sealed record ResumeRequest(
        Guid SessionId,
        // Tenant scope — folded into the bookmark name so the lookup can only match a gate in
        // the caller's own tenant. Supplied by the tenant-scoped Tamma.Api caller.
        string? TenantId,
        // The injected domain input (e.g. the stakeholder's clarifying-question answers).
        string InputJson,
        // Non-repudiation — the acting identity, derived server-side by Tamma.Api from the
        // authenticated principal. Logged here for the audit trail; never trusted from a client.
        string? Respondent);

    /// <summary>Bookmark name the gate registers — delegates to the SINGLE canonical builder
    /// shared with <see cref="WaitForDocumentInputActivity"/> so suspend-side and resume-side
    /// names match byte-for-byte.</summary>
    public static string BookmarkName(ResumeRequest request)
        => WaitForDocumentInputActivity.InputBookmarkName(request.TenantId, request.SessionId);

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.DocumentInputResume");

        if (request.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(request.InputJson))
            return Results.BadRequest(new { error = "inputJson is required" });

        // The name carries the caller's tenant + session. A caller in tenant A produces a name
        // keyed by tenant A and therefore can only ever resolve tenant A's gate.
        var name = BookmarkName(request);

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended document-input bookmark {Bookmark} — gate not waiting (already answered/advanced, wrong session, wrong tenant, or timed out)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No document-input wait is currently suspended for this session.",
            });
        }

        // The name is unique per tenant+session; >1 match is an integrity violation, not a
        // "pick the first" situation. REFUSE rather than resume an arbitrary instance.
        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous document-input bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                name, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = name,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this input bookmark; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

        // Inject the payload using the EXACT keys the suspend-side callback reads
        // (WaitForDocumentInputActivity.ReadInput).
        var input = new Dictionary<string, object>
        {
            ["Received"] = true,
            ["InputJson"] = request.InputJson,
        };

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
            "Resumed document-input gate {Bookmark} (instance {InstanceId}) by {Respondent}",
            name, bookmark.WorkflowInstanceId, request.Respondent ?? "unknown");

        return Results.Ok(new
        {
            resumed = true,
            bookmark = name,
            workflowInstanceId = bookmark.WorkflowInstanceId,
        });
    }
}
