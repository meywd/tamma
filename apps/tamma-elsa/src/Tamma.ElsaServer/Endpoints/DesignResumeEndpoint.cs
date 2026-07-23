using System.Text.Json;
using Elsa.Workflows.Runtime;
using Microsoft.AspNetCore.Mvc;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.ElsaServer.Endpoints;

/// <summary>
/// Story 39-13 (D4) — the design review resume seam, now a THIN ADAPTER onto the generic
/// <see cref="DocumentDecisionResumeEndpoint"/>. The public route contract
/// (<c>POST /elsa/api/adl/design/resume</c>) is preserved byte-stable: same
/// <see cref="ResumeRequest"/> payload (<c>SessionId</c>/<c>TenantId</c>/<c>Approved</c>/
/// <c>Feedback</c>/<c>Reviewer</c>) and same 400/404/409/200 posture. The bespoke
/// <c>WaitForDesignApprovalActivity</c> is retired; design acceptance rides 39-8's generic
/// decision gate on the canonical tenant-folded bookmark
/// (<c>document-decision-{tenant}-{session}</c>), so this adapter TRANSLATES the legacy
/// approve/reject payload into an <see cref="AcceptanceDecision"/> and forwards to the
/// generic decision-resume path. The bookmark lookup / injection / response codes all live in
/// the generic endpoint (the #15/#437 discipline: adapters translate payloads only).
///
/// <para>Mapping: <c>Approved=true</c> → <see cref="AcceptanceDecision.Accept"/>;
/// <c>Approved=false</c> → <see cref="AcceptanceDecision.Reject"/> with <c>reason=Feedback</c>.
/// Channel is <c>user</c> (a human reviewer decides), so <see cref="AcceptanceGuardrails"/>
/// does not clamp the reject to escalate.</para>
/// </summary>
public static class DesignResumeEndpoint
{
    public sealed record ResumeRequest(
        Guid SessionId,
        string? TenantId,
        // The reviewer's decision: true = approve, false = reject.
        bool Approved,
        // The reviewer's feedback (optional; captured on the audit trail either way).
        string? Feedback,
        // Non-repudiation — the acting identity, derived server-side by Tamma.Api. Logged, never trusted.
        string? Reviewer);

    /// <summary>Bookmark name the generic gate registers — the canonical decision-session name
    /// (<c>document-decision-{tenant}-{session}</c>), byte-identical to what the lifecycle's
    /// <c>WaitForDocumentDecisionActivity</c> suspends on for this session.</summary>
    public static string BookmarkName(ResumeRequest request)
        => LifecycleBookmarks.ForDecisionSession(request.TenantId, request.SessionId);

    /// <summary>Translate the legacy approve/reject payload into a canonical
    /// <see cref="AcceptanceDecision"/> json (exposed for the read-back tests).</summary>
    public static string ToDecisionJson(ResumeRequest request)
    {
        AcceptanceDecision decision = request.Approved
            ? new AcceptanceDecision.Accept()
            : new AcceptanceDecision.Reject(request.Feedback ?? string.Empty);
        return JsonSerializer.Serialize(decision, AcceptanceRulesJson.Options);
    }

    public static Task<IResult> Resume(
        [FromBody] ResumeRequest request,
        [FromServices] IBookmarkStore bookmarkStore,
        [FromServices] IWorkflowRuntime workflowRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Empty-session 400 is handled by the generic endpoint (identical posture).
        var generic = new DocumentDecisionResumeEndpoint.ResumeRequest(
            SessionId: request.SessionId,
            TenantId: request.TenantId,
            DecisionJson: request.SessionId == Guid.Empty ? string.Empty : ToDecisionJson(request),
            Feedback: request.Feedback,
            DeciderId: request.Reviewer,
            DeciderDisplay: request.Reviewer,
            Channel: "user",
            RulesReference: null);

        return DocumentDecisionResumeEndpoint.Resume(
            generic, bookmarkStore, workflowRuntime, loggerFactory, ct);
    }
}
