using Elsa.Workflows.Runtime;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Documents;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Story 39-13 (D3) — the clarify answers resume seam, now a THIN ADAPTER onto the generic
/// <see cref="DocumentInputResumeEndpoint"/>. The public route contract
/// (<c>POST /elsa/api/adl/clarify/resume</c>) is preserved byte-stable: same
/// <see cref="ResumeRequest"/> payload (<c>SessionId</c>/<c>TenantId</c>/<c>Answers</c>/
/// <c>Resolver</c>) and same 400/404/409/200 posture. The bespoke
/// <c>WaitForClarifyingAnswersActivity</c> is retired; the wait-for-answers step rides the
/// generic <see cref="WaitForDocumentInputActivity"/> gate on the canonical tenant-folded
/// bookmark (<c>document-input-{tenant}-{session}</c>), so this adapter forwards the answers to
/// the generic input-resume path with keys <c>{Received:true, InputJson:answers}</c>. The
/// bookmark lookup / injection / response codes all live in the generic endpoint.
/// </summary>
public static class ClarifyResumeEndpoint
{
    public sealed record ResumeRequest(
        Guid SessionId,
        string? TenantId,
        // The stakeholder's answers to the clarifying questions.
        string Answers,
        // Non-repudiation — the acting identity, derived server-side by Tamma.Api. Logged, never trusted.
        string? Resolver);

    /// <summary>Bookmark name the generic gate registers — the canonical input-session name
    /// (<c>document-input-{tenant}-{session}</c>).</summary>
    public static string BookmarkName(ResumeRequest request)
        => WaitForDocumentInputActivity.InputBookmarkName(request.TenantId, request.SessionId);

    public static Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var generic = new DocumentInputResumeEndpoint.ResumeRequest(
            SessionId: request.SessionId,
            TenantId: request.TenantId,
            InputJson: request.Answers,
            Respondent: request.Resolver);

        return DocumentInputResumeEndpoint.Resume(
            generic, bookmarkStore, workflowRuntime, loggerFactory, ct);
    }
}
