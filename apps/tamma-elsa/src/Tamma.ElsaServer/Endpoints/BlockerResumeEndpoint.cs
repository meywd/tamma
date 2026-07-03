using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Blocker;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Follow-up #15 — in-process resume seam for the <c>blocker-diagnosis</c> progressive
/// resolution ladder. Mirrors <see cref="MergeApprovalResumeEndpoint"/>.
///
/// <para>The blocker workflow suspends on ONE of two named bookmarks:
/// <list type="bullet">
///   <item><description><b>progress</b> — <c>blocker-progress-{session}-{level}</c>
///     (<see cref="DetectProgressActivity"/>), resumed when the junior makes progress at
///     the Hint / Guidance / Assistance rung; the callback reads
///     <c>ProgressDetected</c> / <c>ProgressType</c> / <c>Details</c> from the workflow
///     input and flips the ladder to the <c>Resolved</c> terminal.</description></item>
///   <item><description><b>escalation</b> — <c>blocker-escalation-{session}</c>
///     (<see cref="EscalateToSeniorActivity"/>), resumed when a senior responds to the
///     Level-4 escalation; the callback reads <c>Resolved</c> / <c>SeniorResponse</c>
///     and flips to the <c>Resolved</c> (or, on the durable SLA, <c>Timeout</c>)
///     terminal.</description></item>
/// </list>
/// Until this endpoint existed, the ONLY resumer (<c>MentorshipController.ResumeSession</c>
/// → generic <c>/resume</c>) supplied NO bookmark id and NO input, so a run could never
/// reach <c>Resolved</c> — only <c>Escalated</c> / <c>Timeout</c>. This endpoint looks the
/// bookmark up by name and runs the owning instance from that bookmark with the progress /
/// escalation payload INJECTED as workflow input (<c>IWorkflowClient.RunInstanceAsync</c>
/// with <c>RunWorkflowInstanceRequest.Input</c>, which lands in
/// <c>ActivityExecutionContext.WorkflowInput</c> that the activity callbacks read).</para>
///
/// <para>This lives in the engine process because <c>IBookmarkStore</c> /
/// <c>IWorkflowRuntime</c> are in-process here. <c>Tamma.Api</c> exposes the RBAC-gated,
/// tenant-scoped public surface (<c>POST /api/adl/blocker/resume</c>) and forwards to this
/// engine endpoint.</para>
///
/// <para><b>SECURITY (mirrors the merge/deploy gate posture):</b>
/// <list type="bullet">
///   <item><description><b>No IDOR</b> — the bookmark name is keyed by the mentorship
///   session id (a 128-bit unguessable Guid). Unlike the merge gate (which folds the
///   tenant into the bookmark name), the blocker bookmark carries only the session id, so
///   the cross-tenant guard is enforced ONE TIER UP: the <c>Tamma.Api</c> caller verifies
///   the caller's ambient tenant OWNS the session (tenant-scoped
///   <c>IMentorshipService.GetSessionAsync</c>) BEFORE forwarding here — a cross-tenant /
///   unknown session 404s at the API tier and never reaches this seam. Resolving the
///   bookmark by that already-tenant-verified session id is therefore only ever the
///   caller's own gate.</description></item>
///   <item><description><b>Collision refusal</b> — the name is unique per session (+ level
///   for progress); &gt;1 match is an integrity violation, so we REFUSE with 409 rather
///   than resume an arbitrary <c>bookmarks[0]</c>.</description></item>
///   <item><description><b>Engine-service-only</b> — the route is mapped with
///   <c>.RequireAuthorization()</c> in <c>Program.cs</c> so only the Tamma.Api→engine hop
///   (presenting the Elsa admin API key) reaches it; anonymous public callers get 401. It
///   is also excluded from the public nginx <c>/elsa/api/</c> block (internal hop only).
///   </description></item>
/// </list></para>
/// </summary>
public static class BlockerResumeEndpoint
{
    /// <summary>The two bookmark families this seam can drive (case-insensitive).</summary>
    private const string KindProgress = "progress";
    private const string KindEscalation = "escalation";

    public sealed record ResumeRequest(
        Guid SessionId,
        // "progress" (Hint/Guidance/Assistance rung) | "escalation" (Level-4 senior response).
        string Kind,
        // Required for Kind=="progress": Hint | Guidance | Assistance.
        string? Level,
        // Kind=="escalation": did the senior actually resolve the blocker (default true).
        bool Resolved,
        // Kind=="progress": how progress was observed (commit | ci | manual | ...).
        string? ProgressType,
        // Kind=="progress": free-text detail.
        string? Details,
        // Kind=="escalation": the senior's response note.
        string? SeniorResponse,
        // Non-repudiation (I2) — the acting identity, derived server-side by Tamma.Api from
        // the authenticated principal. Logged here for the audit trail; never trusted from a
        // client (the public request record carries no resolver field).
        string? Resolver);

    /// <summary>Compute the bookmark name for a request via the SINGLE canonical builders
    /// shared with the suspend-side activities, so suspend and resume match byte-for-byte.
    /// Returns null when the request is malformed (unknown kind / missing-or-bad level).</summary>
    public static string? BookmarkName(ResumeRequest request)
    {
        if (string.Equals(request.Kind, KindEscalation, StringComparison.OrdinalIgnoreCase))
            return EscalateToSeniorActivity.EscalationBookmarkName(request.SessionId);

        if (string.Equals(request.Kind, KindProgress, StringComparison.OrdinalIgnoreCase))
        {
            var level = CanonicalLevel(request.Level);
            return level is null ? null : DetectProgressActivity.ProgressBookmarkName(request.SessionId, level);
        }

        return null;
    }

    /// <summary>Map a case-insensitive level onto the workflow's exact PascalCase segment
    /// ("Hint"/"Guidance"/"Assistance"); null for anything else.</summary>
    internal static string? CanonicalLevel(string? level)
        => level?.Trim().ToLowerInvariant() switch
        {
            "hint" => "Hint",
            "guidance" => "Guidance",
            "assistance" => "Assistance",
            _ => null,
        };

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.BlockerResume");

        if (request.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });

        var isProgress = string.Equals(request.Kind, KindProgress, StringComparison.OrdinalIgnoreCase);
        var isEscalation = string.Equals(request.Kind, KindEscalation, StringComparison.OrdinalIgnoreCase);
        if (!isProgress && !isEscalation)
            return Results.BadRequest(new { error = "kind must be one of: progress, escalation" });
        if (isProgress && CanonicalLevel(request.Level) is null)
            return Results.BadRequest(new { error = "level must be one of: Hint, Guidance, Assistance (for kind=progress)" });

        // Compute the (session-scoped) bookmark name via the shared canonical builder — the
        // same name the suspend-side activity registered.
        var name = BookmarkName(request);
        if (name is null)
            return Results.BadRequest(new { error = "malformed blocker resume request" });

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = name }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            logger.LogWarning(
                "No suspended blocker bookmark {Bookmark} — gate not waiting (already resolved/advanced, wrong session/level, or timed out)",
                name);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = name,
                detail = "No blocker-diagnosis wait is currently suspended for this session/level.",
            });
        }

        // The name is unique per session (+ level); >1 match is an integrity violation, not a
        // "pick the first" situation. REFUSE rather than resume an arbitrary instance.
        if (bookmarks.Count > 1)
        {
            logger.LogError(
                "Ambiguous blocker bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                name, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = name,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this blocker bookmark; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

        // Inject the payload as workflow input using the EXACT keys the suspend-side callback
        // reads (DetectProgressActivity.OnResumeAsync / EscalateToSeniorActivity.OnResumeAsync).
        var input = new Dictionary<string, object>();
        if (isProgress)
        {
            // A progress resume means the junior made progress → flip the ladder to Resolved.
            input["ProgressDetected"] = true;
            if (request.ProgressType is not null) input["ProgressType"] = request.ProgressType;
            if (request.Details is not null) input["Details"] = request.Details;
        }
        else
        {
            input["Resolved"] = request.Resolved;
            if (request.SeniorResponse is not null) input["SeniorResponse"] = request.SeniorResponse;
        }

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
            "Resumed blocker gate {Bookmark} (instance {InstanceId}) kind={Kind} by {Resolver}",
            name, bookmark.WorkflowInstanceId, request.Kind, request.Resolver ?? "unknown");

        return Results.Ok(new
        {
            resumed = true,
            bookmark = name,
            workflowInstanceId = bookmark.WorkflowInstanceId,
            kind = request.Kind,
        });
    }
}
