using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Clarify;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Story 3.5 — in-process resume seam for the <c>clarifying-questions</c> workflow's
/// human-answer gate. Mirrors <see cref="MergeApprovalResumeEndpoint"/> exactly.
///
/// <para>The gate (<see cref="WaitForClarifyingAnswersActivity"/>) suspends on the
/// named bookmark <c>clarify-answers-{tenant}-{session}</c> (tenant folded in) and, on
/// resume, reads <c>{Answered, Answers}</c> from the workflow input. This endpoint looks
/// the bookmark up by name, then runs the owning instance from that bookmark with the
/// answers payload INJECTED as workflow input
/// (<c>IWorkflowClient.RunInstanceAsync</c> with <c>RunWorkflowInstanceRequest.Input</c>,
/// which lands in <c>ActivityExecutionContext.WorkflowInput</c> that the gate reads).</para>
///
/// <para>This lives in the engine process because <c>IBookmarkStore</c> /
/// <c>IWorkflowRuntime</c> are in-process here. <c>Tamma.Api</c> exposes the RBAC-gated,
/// tenant-scoped public surface (<c>POST /api/adl/clarify/resume</c>) and forwards to
/// this engine endpoint.</para>
///
/// <para><b>SECURITY (mirrors the merge/deploy gate posture):</b>
/// <list type="bullet">
///   <item><description><b>No IDOR</b> — the bookmark name carries the tenant id that
///   the (tenant-scoped) <c>Tamma.Api</c> caller supplies (derived server-side from the
///   authenticated principal, never trusted from the client). A caller scoped to tenant
///   A computes a name with tenant A, so it can NEVER resolve tenant B's gate: a
///   cross-tenant attempt simply 404s (bookmark not found), it never acts. The session
///   id is itself an unguessable 128-bit Guid, so within a tenant the name is unguessable
///   too. This is table-free — Story 3.5 mints no clarify-session row (NON-MIGRATION) —
///   yet strictly cross-tenant-safe, exactly like the merge gate.</description></item>
///   <item><description><b>Collision refusal</b> — the name is globally unique
///   (tenant + session); &gt;1 match is an integrity violation, so we REFUSE with 409
///   rather than resume an arbitrary <c>bookmarks[0]</c>.</description></item>
///   <item><description><b>Engine-service-only</b> — the route is mapped with
///   <c>.RequireAuthorization()</c> in <c>Program.cs</c> so only the Tamma.Api→engine hop
///   (presenting the Elsa admin API key) reaches it; anonymous public callers get 401. It
///   is also excluded from the public nginx <c>/elsa/api/</c> block (internal hop only).
///   </description></item>
/// </list></para>
/// </summary>
public static class ClarifyResumeEndpoint
{
    public sealed record ResumeRequest(
        Guid SessionId,
        // Tenant scope — folded into the bookmark name so the lookup can only match a
        // gate in the caller's own tenant. Supplied by the tenant-scoped Tamma.Api caller.
        string? TenantId,
        // The stakeholder's answers to the clarifying questions.
        string Answers,
        // Non-repudiation — the acting identity, derived server-side by Tamma.Api from the
        // authenticated principal. Logged here for the audit trail; never trusted from a
        // client (the public request record carries no resolver field).
        string? Resolver);

    /// <summary>Bookmark name the gate registers — delegates to the SINGLE canonical
    /// builder shared with <see cref="WaitForClarifyingAnswersActivity"/> so suspend-side
    /// and resume-side names match byte-for-byte.</summary>
    public static string BookmarkName(ResumeRequest request)
        => WaitForClarifyingAnswersActivity.AnswersBookmarkName(request.TenantId, request.SessionId);

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.ClarifyResume");

        if (request.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(request.Answers))
            return Results.BadRequest(new { error = "answers is required" });

        // The name carries the caller's tenant + session. A caller in tenant A produces a
        // name keyed by tenant A and therefore can only ever resolve tenant A's gate.
        var name = BookmarkName(request);

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended clarify bookmark {Bookmark} — gate not waiting (already answered/advanced, wrong session, wrong tenant, or timed out)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No clarifying-questions wait is currently suspended for this session.",
            });
        }

        // The name is unique per tenant+session; >1 match is an integrity violation, not a
        // "pick the first" situation. REFUSE rather than resume an arbitrary instance.
        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous clarify bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                name, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = name,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this clarify bookmark; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

        // Inject the payload using the EXACT keys the suspend-side callback reads
        // (WaitForClarifyingAnswersActivity.ReadAnswers).
        var input = new Dictionary<string, object>
        {
            ["Answered"] = true,
            ["Answers"] = request.Answers,
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
            "Resumed clarify gate {Bookmark} (instance {InstanceId}) by {Resolver}",
            name, bookmark.WorkflowInstanceId, request.Resolver ?? "unknown");

        return Results.Ok(new
        {
            resumed = true,
            bookmark = name,
            workflowInstanceId = bookmark.WorkflowInstanceId,
        });
    }
}
