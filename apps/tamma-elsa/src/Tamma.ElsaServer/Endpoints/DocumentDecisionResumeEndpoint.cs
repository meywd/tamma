using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Documents;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Story 39-8 (AC5) — the generic engine-side resume seam for the ONE document-decision
/// gate (<see cref="WaitForDocumentDecisionActivity"/>). Mirrors
/// <see cref="DesignResumeEndpoint"/> byte-for-byte in security posture; it generalizes the
/// convention the five existing <c>*ResumeEndpoint.cs</c> prove (which stay as-is — this is
/// EXTRACTION, not migration).
///
/// <para>The gate suspends on the tenant-folded bookmark
/// <c>document-decision-{tenant}-{session}</c> and, on resume, reads
/// <c>{DecisionJson, Feedback, DeciderId, DeciderDisplay, Channel, RulesReference}</c> from
/// the workflow input. This endpoint looks the bookmark up by name, then runs the owning
/// instance from that bookmark with the decision payload INJECTED as workflow input
/// (<c>IWorkflowClient.RunInstanceAsync</c> with <c>RunWorkflowInstanceRequest.Input</c>,
/// which lands in <c>ActivityExecutionContext.WorkflowInput</c> the gate reads). The gate
/// branches to its <c>Accept</c>/<c>RequestRevision</c>/<c>Reject</c>/<c>Escalate</c> outcome
/// off the mapped 39-5 <c>AcceptanceDecision</c>.</para>
///
/// <para>Self-decision (orchestrator) and assigned-human decision resume this SAME bookmark —
/// only the server-derived <c>DeciderId</c>/<c>Channel</c> vary; the gate is identical
/// (AC4).</para>
///
/// <para><b>SECURITY (mirrors the design/merge/deploy/clarify gate posture):</b>
/// <list type="bullet">
///   <item><description><b>No IDOR</b> — the bookmark name carries the tenant id the
///   (tenant-scoped) <c>Tamma.Api</c> caller supplies (derived server-side, never trusted
///   from the client). A caller scoped to tenant A computes a name with tenant A, so it can
///   NEVER resolve tenant B's gate: a cross-tenant attempt simply 404s. The session id is an
///   unguessable 128-bit Guid, so within a tenant the name is unguessable too.</description></item>
///   <item><description><b>Collision refusal</b> — the name is globally unique (tenant +
///   session); &gt;1 match is an integrity violation, so we REFUSE with 409 rather than
///   resume an arbitrary <c>bookmarks[0]</c>.</description></item>
///   <item><description><b>Engine-service-only</b> — the route is mapped with
///   <c>.RequireAuthorization()</c> in <c>Program.cs</c> so only the Tamma.Api→engine hop
///   reaches it; anonymous public callers get 401. It is also excluded from the public nginx
///   <c>/elsa/api/</c> block (internal hop only).</description></item>
/// </list></para>
/// </summary>
public static class DocumentDecisionResumeEndpoint
{
    public sealed record ResumeRequest(
        Guid SessionId,
        // Tenant scope — folded into the bookmark name so the lookup can only match a gate in
        // the caller's own tenant. Supplied by the tenant-scoped Tamma.Api caller.
        string? TenantId,
        // The serialized 39-5 AcceptanceDecision (validated + built server-side by Tamma.Api).
        string DecisionJson,
        // Decider feedback / notes (optional; captured on the audit trail either way).
        string? Feedback,
        // Non-repudiation — the acting identity, derived server-side by Tamma.Api from the
        // authenticated principal. Never trusted from a client.
        string? DeciderId,
        string? DeciderDisplay,
        // The server-derived transport channel (orchestrator|user|api) — D6, never body-trusted.
        string Channel,
        // The resolved-rules reference the decision was made under (server-stamped).
        string? RulesReference);

    /// <summary>Bookmark name the gate registers — delegates to the SINGLE canonical builder
    /// shared with <see cref="WaitForDocumentDecisionActivity"/> so suspend-side and resume-side
    /// names match byte-for-byte.</summary>
    public static string BookmarkName(ResumeRequest request)
        => WaitForDocumentDecisionActivity.DecisionBookmarkName(request.TenantId, request.SessionId);

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.DocumentDecisionResume");

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
                "No suspended document-decision bookmark {Bookmark} — gate not waiting (already decided/advanced, wrong session, or wrong tenant)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No document decision is currently suspended for this session.",
            });
        }

        // The name is unique per tenant+session; >1 match is an integrity violation, not a
        // "pick the first" situation. REFUSE rather than resume an arbitrary instance.
        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous document-decision bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                name, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = name,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this decision bookmark; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

        // Inject the payload using the EXACT keys the suspend-side callback reads
        // (WaitForDocumentDecisionActivity.ReadDecision).
        var input = new Dictionary<string, object>
        {
            ["DecisionJson"] = request.DecisionJson ?? string.Empty,
            ["Feedback"] = request.Feedback ?? string.Empty,
            ["DeciderId"] = request.DeciderId ?? string.Empty,
            ["DeciderDisplay"] = request.DeciderDisplay ?? string.Empty,
            ["Channel"] = request.Channel ?? string.Empty,
            ["RulesReference"] = request.RulesReference ?? string.Empty,
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
            "Resumed document-decision gate {Bookmark} (instance {InstanceId}) channel={Channel} decider={Decider}",
            name, bookmark.WorkflowInstanceId, request.Channel, request.DeciderId ?? "unknown");

        return Results.Ok(new
        {
            resumed = true,
            bookmark = name,
            workflowInstanceId = bookmark.WorkflowInstanceId,
        });
    }
}
