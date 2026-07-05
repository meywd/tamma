using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Design;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Story 3.7 — in-process resume seam for the <c>design-proposal</c> workflow's human
/// review gate. Mirrors <see cref="ClarifyResumeEndpoint"/> exactly.
///
/// <para>The gate (<see cref="WaitForDesignApprovalActivity"/>) suspends on the named
/// bookmark <c>design-approval-{tenant}-{session}</c> (tenant folded in) and, on resume,
/// reads <c>{Approved, Feedback}</c> from the workflow input. This endpoint looks the
/// bookmark up by name, then runs the owning instance from that bookmark with the decision
/// payload INJECTED as workflow input (<c>IWorkflowClient.RunInstanceAsync</c> with
/// <c>RunWorkflowInstanceRequest.Input</c>, which lands in
/// <c>ActivityExecutionContext.WorkflowInput</c> that the gate reads). The gate branches to
/// its <c>Approved</c> or <c>Rejected</c> outcome off the injected <c>Approved</c> flag.</para>
///
/// <para>This lives in the engine process because <c>IBookmarkStore</c> /
/// <c>IWorkflowRuntime</c> are in-process here. <c>Tamma.Api</c> exposes the RBAC-gated,
/// tenant-scoped public surface (<c>POST /api/adl/design/resume</c>) and forwards to this
/// engine endpoint.</para>
///
/// <para><b>SECURITY (mirrors the merge/deploy/clarify gate posture):</b>
/// <list type="bullet">
///   <item><description><b>No IDOR</b> — the bookmark name carries the tenant id that the
///   (tenant-scoped) <c>Tamma.Api</c> caller supplies (derived server-side from the
///   authenticated principal, never trusted from the client). A caller scoped to tenant A
///   computes a name with tenant A, so it can NEVER resolve tenant B's gate: a cross-tenant
///   attempt simply 404s (bookmark not found), it never acts. The session id is itself an
///   unguessable 128-bit Guid, so within a tenant the name is unguessable too. This is
///   table-free — Story 3.7 mints no design-proposal row (NON-MIGRATION) — yet strictly
///   cross-tenant-safe, exactly like the merge gate.</description></item>
///   <item><description><b>Collision refusal</b> — the name is globally unique (tenant +
///   session); &gt;1 match is an integrity violation, so we REFUSE with 409 rather than
///   resume an arbitrary <c>bookmarks[0]</c>.</description></item>
///   <item><description><b>Engine-service-only</b> — the route is mapped with
///   <c>.RequireAuthorization()</c> in <c>Program.cs</c> so only the Tamma.Api→engine hop
///   (presenting the Elsa admin API key) reaches it; anonymous public callers get 401. It is
///   also excluded from the public nginx <c>/elsa/api/</c> block (internal hop only).
///   </description></item>
/// </list></para>
/// </summary>
public static class DesignResumeEndpoint
{
    public sealed record ResumeRequest(
        Guid SessionId,
        // Tenant scope — folded into the bookmark name so the lookup can only match a gate in
        // the caller's own tenant. Supplied by the tenant-scoped Tamma.Api caller.
        string? TenantId,
        // The reviewer's decision: true = approve, false = reject.
        bool Approved,
        // The reviewer's feedback (optional; captured on the audit trail either way).
        string? Feedback,
        // Non-repudiation — the acting identity, derived server-side by Tamma.Api from the
        // authenticated principal. Logged here for the audit trail; never trusted from a
        // client (the public request record carries no reviewer field).
        string? Reviewer);

    /// <summary>Bookmark name the gate registers — delegates to the SINGLE canonical builder
    /// shared with <see cref="WaitForDesignApprovalActivity"/> so suspend-side and resume-side
    /// names match byte-for-byte.</summary>
    public static string BookmarkName(ResumeRequest request)
        => WaitForDesignApprovalActivity.ApprovalBookmarkName(request.TenantId, request.SessionId);

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.DesignResume");

        if (request.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });

        // The name carries the caller's tenant + session. A caller in tenant A produces a name
        // keyed by tenant A and therefore can only ever resolve tenant A's gate.
        var name = BookmarkName(request);

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended design bookmark {Bookmark} — gate not waiting (already decided/advanced, wrong session, wrong tenant, or timed out)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No design-proposal review is currently suspended for this session.",
            });
        }

        // The name is unique per tenant+session; >1 match is an integrity violation, not a
        // "pick the first" situation. REFUSE rather than resume an arbitrary instance.
        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous design bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                name, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = name,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this design bookmark; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

        // Inject the payload using the EXACT keys the suspend-side callback reads
        // (WaitForDesignApprovalActivity.ReadDecision).
        var input = new Dictionary<string, object>
        {
            ["Approved"] = request.Approved,
            ["Feedback"] = request.Feedback ?? string.Empty,
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
            "Resumed design gate {Bookmark} (instance {InstanceId}) approved={Approved} by {Reviewer}",
            name, bookmark.WorkflowInstanceId, request.Approved, request.Reviewer ?? "unknown");

        return Results.Ok(new
        {
            resumed = true,
            bookmark = name,
            workflowInstanceId = bookmark.WorkflowInstanceId,
            approved = request.Approved,
        });
    }
}
