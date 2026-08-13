using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Epic 31 P4 M2 (DG-6) — in-process resume seam for the cycle's
/// <c>WaitForPRMerged</c> wait: the merged-PR webhook becomes the PRIMARY
/// merge-confirmation source; the 12h TimedOut SLA stays as the exception
/// path. Before this endpoint existed NOTHING resumed the <c>pr-merged</c>
/// bookmark — every merged PR ended its cycle through the 12h timeout with a
/// needs-human handoff, on every platform including GitHub.
///
/// <para>Callers: Tamma.Api's <c>PrMergedWebhookHandler</c>s (GitHub
/// <c>pull_request.closed(merged=true)</c>, Gitea/Forgejo equivalent, GitLab
/// <c>merge_request action=merge</c>) over the internal Tamma.Api→engine hop.
/// Mirrors <see cref="MergeApprovalResumeEndpoint"/>: same auth model
/// (<c>RequireAuthorization()</c> — the Elsa admin API key; excluded from the
/// public nginx <c>/elsa/api/</c> block), same bookmark-name scoping, same
/// collision refusal.</para>
///
/// <para><b>SECURITY / bookmark naming (rollout-safe transition):</b></para>
/// <list type="bullet">
///   <item><description><b>Qualified name first</b> —
///   <see cref="WaitForPRMergedActivity.BookmarkName"/> folds tenant + repo in
///   (the merge-approval C1/C2 convention): a caller scoped to tenant A
///   computes a name with tenant A and can never resolve tenant B's wait.
///   New suspensions register ONLY this name.</description></item>
///   <item><description><b>Legacy fallback second</b> — instances suspended
///   BEFORE this deploy hold the unqualified
///   <see cref="WaitForPRMergedActivity.LegacyBookmarkName"/>
///   (<c>pr-merged-{n}</c>). The resumer matches it only when the qualified
///   lookup found nothing, and REFUSES on ambiguity (two live instances on
///   the same PR number → 409, never "pick the first"). The 12h SLA bounds
///   this transition window.</description></item>
///   <item><description><b>404 = idempotent no-op</b> — a burned bookmark
///   (the SLA edge fired first, or a duplicate delivery raced) resolves
///   nothing and never double-advances the workflow.</description></item>
///   <item><description><b>Atomic claim before running</b> (Epic 31 review,
///   F-critical — same defect as <see cref="CiWaitEndpoints"/>, see its type
///   doc for the full mechanics): the consumed bookmark row is only deleted
///   when the resumed burst COMMITS, so two concurrent deliveries (webhook
///   redelivery vs the 12h SLA edge vs an HTTP retry) could both find the
///   row and run the continuation twice in parallel. The resumer deletes the
///   row by id FIRST — one conditional <c>DELETE</c>, exactly one winner —
///   and only the winner runs the instance; a claim whose run fails restores
///   the row so the wait keeps its SLA.</description></item>
/// </list>
/// </summary>
public static class PrMergedResumeEndpoint
{
    public sealed record ResumeRequest(
        int PrNumber,
        string? MergeSha,
        // Tenant + repository scope the bookmark lookup — supplied by the
        // (receiver-resolved) Tamma.Api webhook handler, folded into the
        // qualified bookmark name so the lookup can only match the caller's
        // own tenant/repo.
        string? TenantId,
        string? Repository);

    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.PrMergedResume");

        if (request.PrNumber <= 0)
            return Results.BadRequest(new { error = "prNumber must be positive" });

        // 1) Qualified (tenant+repo) name — the designed path.
        var qualified = WaitForPRMergedActivity.BookmarkName(
            request.TenantId, request.Repository, request.PrNumber);
        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = qualified }, ct)
            .ConfigureAwait(false)).ToList();
        var matchedName = qualified;

        // 2) Transition fallback — the pre-P4 unqualified name. Only consulted
        //    when the qualified lookup finds nothing; ambiguity refuses.
        if (bookmarks.Count == 0)
        {
            var legacy = WaitForPRMergedActivity.LegacyBookmarkName(request.PrNumber);
            bookmarks = (await bookmarkStore
                .FindManyAsync(new BookmarkFilter { Name = legacy }, ct)
                .ConfigureAwait(false)).ToList();
            matchedName = legacy;
        }

        if (bookmarks.Count == 0)
        {
            // Idempotency: the SLA edge (or an earlier delivery) burned the
            // bookmark — a late merge webhook is a benign no-op.
            logger.LogInformation(
                "No suspended pr-merged bookmark {Bookmark} — wait already resumed, timed out, or not this tenant/repo",
                qualified);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = qualified,
                detail = "No PR-merged wait is currently suspended for this PR.",
            });
        }

        if (bookmarks.Count > 1)
        {
            // The qualified name is globally unique; the legacy name is only
            // unique per PR number. Either way, >1 live instance is an
            // integrity violation — REFUSE rather than resume an arbitrary one
            // (the MergeApprovalResumeEndpoint C2 rule).
            logger.LogError(
                "Ambiguous pr-merged bookmark {Bookmark}: {Count} live instances — refusing to resume an arbitrary one",
                matchedName, bookmarks.Count);
            return Results.Conflict(new
            {
                error = "ambiguous_bookmark",
                bookmark = matchedName,
                count = bookmarks.Count,
                detail = "Multiple workflow instances are waiting on this PR; refusing to resume an arbitrary one.",
            });
        }

        var bookmark = bookmarks[0];

        // Atomic claim — serialize concurrent resumes for this bookmark (see
        // the type doc): exactly one caller deletes the row; the rest see 0
        // and treat the wait as already-resumed. Never run without a claim.
        var claimed = (await bookmarkStore
            .DeleteAsync(new BookmarkFilter { BookmarkId = bookmark.Id }, ct)
            .ConfigureAwait(false)) > 0;

        if (!claimed)
        {
            logger.LogInformation(
                "pr-merged bookmark {Bookmark} claimed by a concurrent resume — this caller is a benign no-op",
                matchedName);
            return Results.NotFound(new
            {
                error = "bookmark_not_found",
                bookmark = qualified,
                detail = "No PR-merged wait is currently suspended for this PR.",
            });
        }

        // The activity's OnMerged callback reads WorkflowInput["mergeSha"].
        var input = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(request.MergeSha))
            input["mergeSha"] = request.MergeSha;

        var client = await workflowRuntime
            .CreateClientAsync(bookmark.WorkflowInstanceId, ct)
            .ConfigureAwait(false);

        try
        {
            await client.RunInstanceAsync(
                new RunWorkflowInstanceRequest
                {
                    BookmarkId = bookmark.Id,
                    Input = input,
                },
                ct).ConfigureAwait(false);
        }
        catch
        {
            // The claim consumed the row but the continuation never reached a
            // commit — restore the row (not the caller's token: a cancelled
            // restore is a restore that never happened) so the wait keeps its
            // 12h SLA and a webhook redelivery can retry.
            await bookmarkStore.SaveAsync(bookmark, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation(
            "Resumed pr-merged wait {Bookmark} (instance {InstanceId}) on the Merged edge with sha {MergeSha}",
            matchedName, bookmark.WorkflowInstanceId, request.MergeSha ?? "<none>");

        return Results.Ok(new
        {
            resumed = true,
            bookmark = matchedName,
            workflowInstanceId = bookmark.WorkflowInstanceId,
            mergeSha = request.MergeSha,
        });
    }
}
