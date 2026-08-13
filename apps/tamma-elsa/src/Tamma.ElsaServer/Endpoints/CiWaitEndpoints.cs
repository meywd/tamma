using System.Text.Json;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Testing;
using Tamma.Activities.Testing.Models;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Epic 31 P3 (DG-5) — the engine-side half of the CI completion poller.
///
/// <para><see cref="WaitForCIResultsActivity"/> suspends on a bookmark
/// registered under the COMMON stimulus name
/// <see cref="WaitForCIResultsActivity.CiWaitStimulusName"/>, whose payload
/// (<see cref="CIResultBookmarkPayload"/>) carries the run id, repository and
/// tenant. Until this seam existed NOTHING resumed that bookmark — only the
/// 30-minute timeout ended the wait. Tamma.Api's
/// <c>CiCompletionPollerService</c> now:</para>
/// <list type="number">
///   <item>GETs <see cref="ListWaits"/> to enumerate suspended CI waits;</item>
///   <item>polls the run's status through the tenant's resolved platform
///     driver (API-side — the engine holds no platform credential);</item>
///   <item>POSTs <see cref="Resume"/> with the terminal result, which runs the
///     owning instance from the bookmark with the result injected as workflow
///     input (the same <c>IWorkflowClient.RunInstanceAsync</c> mechanism as
///     <see cref="MergeApprovalResumeEndpoint"/>).</item>
/// </list>
///
/// <para><b>Idempotency against the timeout race.</b> Elsa auto-burns the
/// activity's bookmarks when the activity completes: if the timeout edge (or a
/// concurrent poller tick) already advanced the wait, the CI-result bookmark id
/// no longer resolves and <see cref="Resume"/> answers 404
/// (<c>bookmark_not_found</c>) WITHOUT touching the instance — a late resume
/// can never double-advance the workflow. Resume targets the exact
/// <c>BookmarkId</c> (globally unique), so there is no ambiguous-match arm.</para>
///
/// <para><b>Concurrent-resume serialization (Epic 31 review, F-critical).</b>
/// The 404 guard above only covers resumes arriving AFTER an earlier one
/// COMMITS. Elsa's local runtime deletes the consumed bookmark's store row
/// only when the resumed burst's state commits at the END of the run, so for
/// the whole continuation the row stays visible and a second caller (the 30s
/// poller tick, the webhook accelerator, an HTTP retry after the client
/// timeout) would load the still-suspended state and execute the SAME
/// continuation a second time in parallel. <see cref="Resume"/> therefore
/// CLAIMS the bookmark row atomically BEFORE running: it deletes the row by
/// id (the EF store issues one conditional <c>DELETE</c>), and only the
/// single caller that observed a row actually deleted proceeds to
/// <c>RunInstanceAsync</c>; every other concurrent caller observes 0 rows
/// and takes the idempotent 404 path. The resume itself never needs the row
/// — the runner schedules the bookmark from the instance's persisted
/// <c>WorkflowState</c>. A claim whose run then FAILS restores the row so
/// the wait stays discoverable (poller retry / timeout SLA).</para>
///
/// <para>Auth: mapped with <c>RequireAuthorization()</c> in Program.cs — the
/// only legitimate caller is the Tamma.Api→engine hop presenting the Elsa
/// admin API key. Same engine-control-surface rationale as the merge/deploy
/// resume seams.</para>
/// </summary>
public static class CiWaitEndpoints
{
    /// <summary>One suspended CI wait, as surfaced to the poller.</summary>
    public sealed record CiWaitDto(
        string BookmarkId,
        string WorkflowInstanceId,
        Guid SessionId,
        string RunId,
        string Repository,
        string? TenantId,
        DateTimeOffset CreatedAt);

    public sealed record ResumeRequest(
        string BookmarkId,
        string? RunId,
        string? Status,
        bool BuildPassed);

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// GET — enumerate every suspended CI-result wait. Waits whose payload
    /// carries no repository (pre-P3 bookmarks) or no usable run id are
    /// EXCLUDED — the poller cannot poll them; they end via the timeout edge
    /// exactly as before.
    /// </summary>
    public static async Task<IResult> ListWaits(
        [FromServices] IBookmarkStore bookmarkStore,
        CancellationToken ct)
    {
        var bookmarks = await bookmarkStore
            .FindManyAsync(new BookmarkFilter { Name = WaitForCIResultsActivity.CiWaitStimulusName }, ct)
            .ConfigureAwait(false);

        var waits = new List<CiWaitDto>();
        foreach (var bookmark in bookmarks)
        {
            var payload = DeserializePayload(bookmark.Payload);
            if (payload is null) continue;
            if (string.IsNullOrWhiteSpace(payload.Repository)) continue;
            if (string.IsNullOrWhiteSpace(payload.RunId)
                || string.Equals(payload.RunId, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            waits.Add(new CiWaitDto(
                BookmarkId: bookmark.Id,
                WorkflowInstanceId: bookmark.WorkflowInstanceId,
                SessionId: payload.SessionId,
                RunId: payload.RunId,
                Repository: payload.Repository,
                TenantId: payload.TenantId,
                CreatedAt: bookmark.CreatedAt));
        }

        return Results.Ok(new { waits });
    }

    /// <summary>
    /// POST — resume ONE suspended CI wait with the observed terminal result.
    /// The input keys mirror what <c>WaitForCIResultsActivity.OnResumeAsync</c>
    /// reads from <c>WorkflowInput</c> (<c>Status</c>, <c>BuildPassed</c>, …).
    /// A burned/unknown bookmark id ⇒ 404 (the timeout or a sibling tick won
    /// the race) — never an error, never a double-advance.
    /// </summary>
    public static async Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.ElsaServer.CiWaitResume");

        if (string.IsNullOrWhiteSpace(request.BookmarkId))
            return Results.BadRequest(new { error = "bookmarkId is required" });

        var bookmarks = (await bookmarkStore
            .FindManyAsync(new BookmarkFilter { BookmarkId = request.BookmarkId }, ct)
            .ConfigureAwait(false)).ToList();

        if (bookmarks.Count == 0)
        {
            // The idempotency guard: the bookmark was burned (timeout edge, or
            // an earlier resume) — a late completion signal is a benign no-op.
            logger.LogDebug(
                "CI-wait bookmark {BookmarkId} not found — already resumed or timed out; nothing to do",
                request.BookmarkId);
            return Results.NotFound(new { error = "bookmark_not_found", bookmarkId = request.BookmarkId });
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
                "CI-wait bookmark {BookmarkId} claimed by a concurrent resume — this caller is a benign no-op",
                bookmark.Id);
            return Results.NotFound(new { error = "bookmark_not_found", bookmarkId = request.BookmarkId });
        }

        var input = new Dictionary<string, object>
        {
            ["Status"] = request.Status ?? "Unknown",
            ["BuildPassed"] = request.BuildPassed,
        };

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
            // restore is a restore that never happened) so the wait stays
            // discoverable for the poller / keeps its timeout SLA.
            await bookmarkStore.SaveAsync(bookmark, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation(
            "Resumed CI wait {BookmarkId} (instance {InstanceId}) with status {Status} (buildPassed={BuildPassed})",
            bookmark.Id, bookmark.WorkflowInstanceId, request.Status, request.BuildPassed);

        return Results.Ok(new
        {
            resumed = true,
            bookmarkId = bookmark.Id,
            workflowInstanceId = bookmark.WorkflowInstanceId,
        });
    }

    /// <summary>Tolerant payload projection — the store may hand back the live
    /// payload object (memory store) or a deserialized JSON shape (EF store);
    /// a round-trip through the serializer normalizes both.</summary>
    internal static CIResultBookmarkPayload? DeserializePayload(object? payload)
    {
        if (payload is null) return null;
        if (payload is CIResultBookmarkPayload typed) return typed;
        try
        {
            var json = payload as string ?? JsonSerializer.Serialize(payload);
            return JsonSerializer.Deserialize<CIResultBookmarkPayload>(json, PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
