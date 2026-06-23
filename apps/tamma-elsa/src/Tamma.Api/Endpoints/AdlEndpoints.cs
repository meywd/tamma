using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services;
using Tamma.Core.Logging;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// IMPORTANT-2 — RBAC-gated public surface that lets a human DRIVE the
/// <c>merge-approval</c> gate of the autonomous loop.
///
/// <para>The gate (<c>WaitForMergeApprovalActivity</c>) suspends the
/// <c>single-issue-cycle</c> on the named bookmark
/// <c>adl-merge-approval-{tenant}-{repo}-{issue}-{pr}</c> and reads
/// <c>{decision, feedback, approver}</c> from the workflow input on resume.
/// Without a producer, wiring the gate into the cycle would suspend the merge
/// step forever. This endpoint is that producer: it forwards to the engine's
/// in-process resume seam (<c>POST /elsa/api/adl/merge-approval/resume</c>),
/// which looks the bookmark up and runs the owning instance with the decision
/// payload injected.</para>
///
/// <para><b>Authorization</b>: <c>WorkflowsManage</c> (permission
/// <c>workflows:manage</c> → tenant owner/admin). Member-role SaaS callers hit
/// 403 at the policy — driving a merge decision is a workflow-management
/// action, mirroring the engine command/issue-mutation routes.</para>
///
/// <para><b>SECURITY C1 (cross-tenant IDOR)</b>: <c>WorkflowsManage</c> alone is
/// any tenant owner/admin, so the prior handler — which took no
/// <see cref="ITenantContext"/> and did no ownership check — let a tenant
/// owner/admin approve/merge/reject ANOTHER tenant's gate by supplying
/// issue+PR. The resume is now tenant-scoped end to end: the caller's ambient
/// tenant id (and the repository) are threaded to the engine, which folds them
/// into the bookmark name. A caller in tenant A can therefore only ever resolve
/// tenant A's gate; a cross-tenant attempt simply 404s (gate not found), it
/// never acts (mirrors <c>WorkflowEndpoints.GetInstanceEvents</c>, finding 016).</para>
///
/// <para><b>SECURITY I2 (non-repudiation)</b>: <c>approver</c> is derived from
/// the authenticated principal server-side; any client-supplied approver is
/// ignored. The DCB audit trail records WHO actually approved.</para>
/// </summary>
public static class AdlEndpoints
{
    /// <summary>Decisions the gate accepts (case-insensitive). Anything else is
    /// rejected with 400 up front so the caller never forwards an arbitrary
    /// string into the gate (MINOR).</summary>
    private static readonly HashSet<string> AllowedDecisions =
        new(StringComparer.OrdinalIgnoreCase) { "merge", "test", "reject" };

    /// <summary>
    /// Resume request. <c>Approver</c> is intentionally absent — the approver is
    /// derived from the authenticated caller (I2), never trusted from the client.
    /// </summary>
    public sealed record MergeApprovalDecisionRequest(
        int IssueNumber,
        int PrNumber,
        string Repository,
        string Decision,
        string? Feedback);

    /// <summary>
    /// Resume the merge-approval gate for an issue/PR with a human decision.
    /// Route: <c>POST /api/adl/merge-approval/resume</c>.
    /// </summary>
    public static async Task<IResult> ResumeMergeApproval(
        [FromBody] MergeApprovalDecisionRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.AdlEndpoints");

        if (string.IsNullOrWhiteSpace(req.Decision))
            return Results.BadRequest(new { error = "decision is required (merge|test|reject)" });
        // MINOR — validate the decision against the accepted set; don't forward an
        // arbitrary string that would silently escalate at the gate.
        if (!AllowedDecisions.Contains(req.Decision.Trim()))
            return Results.BadRequest(new { error = "decision must be one of: merge, test, reject" });
        if (req.IssueNumber <= 0 || req.PrNumber <= 0)
            return Results.BadRequest(new { error = "issueNumber and prNumber are required (> 0)" });
        if (string.IsNullOrWhiteSpace(req.Repository))
            return Results.BadRequest(new { error = "repository is required (owner/repo)" });

        // SECURITY C1 — scope the resume to the caller's ambient tenant. The
        // engine folds this tenant id into the bookmark name, so a caller can
        // only ever resolve a gate in its OWN tenant. (Ambient null = self-hosted
        // single-user scope, where there is no cross-tenant surface.)
        var tenantId = tenantContext.TenantId?.ToString();

        // SECURITY I2 — the approver is the authenticated caller, derived
        // server-side. A client-supplied approver is never honoured so the audit
        // trail can't be forged.
        var approver = ResolveApprover(principal);

        try
        {
            var result = await elsa.ResumeMergeApprovalAsync(
                req.IssueNumber, req.PrNumber, tenantId, req.Repository,
                req.Decision, req.Feedback, approver);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No merge-approval gate is currently suspended for this issue/PR.",
                });
            }

            logger.LogInformation(
                "Drove merge-approval gate for issue #{Issue} PR #{Pr} with decision {Decision} (approver {Approver})",
                req.IssueNumber, req.PrNumber, LogSanitizer.Clean(req.Decision), LogSanitizer.Clean(approver));

            return Results.Ok(new
            {
                resumed = result.Resumed,
                workflowInstanceId = result.WorkflowInstanceId,
                decision = req.Decision,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resume merge-approval gate for issue #{Issue} PR #{Pr}",
                req.IssueNumber, req.PrNumber);
            return Results.Problem(
                detail: "Failed to resume the merge-approval gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Derive the approver identity from the authenticated principal (I2):
    /// email → display name → subject id. Falls back to <c>"unknown"</c> only if
    /// the principal carries none of these (it should never, behind auth).
    /// </summary>
    internal static string ResolveApprover(ClaimsPrincipal principal)
        => principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
           ?? principal.FindFirst(ClaimTypes.Email)?.Value
           ?? principal.FindFirst("name")?.Value
           ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
           ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? "unknown";
}
