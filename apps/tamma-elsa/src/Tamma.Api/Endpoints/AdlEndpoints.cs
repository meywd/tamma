using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services;
using Tamma.Core.Interfaces;
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

    /// <summary>Decisions the production-deploy gate accepts (case-insensitive).
    /// Anything else is rejected with 400 up front so the caller never forwards an
    /// arbitrary string that would silently route to the prod-failure terminal.</summary>
    private static readonly HashSet<string> AllowedDeployDecisions =
        new(StringComparer.OrdinalIgnoreCase) { "approve", "reject" };

    /// <summary>
    /// Resume request for the production-deploy approval gate. <c>Approver</c> is
    /// intentionally absent — derived from the authenticated caller (I2), never
    /// trusted from the client.
    /// </summary>
    public sealed record DeployApprovalDecisionRequest(
        int IssueNumber,
        string Repository,
        string MergeSha,
        string Decision,
        string? Feedback);

    /// <summary>
    /// Resume the production-deploy approval gate for an issue with a human
    /// decision (completeness audit P0 item 3).
    /// Route: <c>POST /api/adl/deploy-approval/resume</c>.
    /// </summary>
    public static async Task<IResult> ResumeDeploymentApproval(
        [FromBody] DeployApprovalDecisionRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.AdlEndpoints");

        if (string.IsNullOrWhiteSpace(req.Decision))
            return Results.BadRequest(new { error = "decision is required (approve|reject)" });
        if (!AllowedDeployDecisions.Contains(req.Decision.Trim()))
            return Results.BadRequest(new { error = "decision must be one of: approve, reject" });
        if (req.IssueNumber <= 0)
            return Results.BadRequest(new { error = "issueNumber is required (> 0)" });
        if (string.IsNullOrWhiteSpace(req.Repository))
            return Results.BadRequest(new { error = "repository is required (owner/repo)" });
        if (string.IsNullOrWhiteSpace(req.MergeSha))
            return Results.BadRequest(new { error = "mergeSha is required" });

        // Scope the resume to the caller's ambient tenant — the engine folds it
        // into the bookmark name so a caller can only resolve a gate in its OWN
        // tenant. (Ambient null = self-hosted single-user scope.)
        var tenantId = tenantContext.TenantId?.ToString();

        // I2 — the approver is the authenticated caller, derived server-side. A
        // client-supplied approver is never honoured so the audit can't be forged.
        var approver = ResolveApprover(principal);

        try
        {
            var result = await elsa.ResumeDeploymentApprovalAsync(
                req.IssueNumber, tenantId, req.Repository, req.MergeSha,
                req.Decision, req.Feedback, approver);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No production-deploy approval gate is currently suspended for this issue/SHA.",
                });
            }

            logger.LogInformation(
                "Drove deploy-approval gate for issue #{Issue} with decision {Decision} (approver {Approver})",
                req.IssueNumber, LogSanitizer.Clean(req.Decision), LogSanitizer.Clean(approver));

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
                "Failed to resume deploy-approval gate for issue #{Issue}", req.IssueNumber);
            return Results.Problem(
                detail: "Failed to resume the production-deploy approval gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ================================================================
    // Blocker-diagnosis progressive-resolution ladder (follow-up #15) —
    // POST /api/adl/blocker/resume. Same RBAC (WorkflowsManage) + I2
    // (server-derived resolver) posture as the merge/deploy gates. The
    // cross-tenant guard differs in MECHANISM only: the blocker bookmark is
    // keyed by the (unguessable) session id, not the tenant, so we enforce
    // ownership by a tenant-scoped session lookup BEFORE forwarding (a
    // cross-tenant / unknown session 404s and never reaches the engine).
    // ================================================================

    /// <summary>The blocker ladder resume kinds accepted (case-insensitive).</summary>
    private static readonly HashSet<string> AllowedBlockerKinds =
        new(StringComparer.OrdinalIgnoreCase) { "progress", "escalation" };

    /// <summary>The progress-ladder levels accepted (case-insensitive; canonicalised to the
    /// workflow's PascalCase before forwarding).</summary>
    private static readonly HashSet<string> AllowedBlockerLevels =
        new(StringComparer.OrdinalIgnoreCase) { "Hint", "Guidance", "Assistance" };

    /// <summary>
    /// Resume request for the blocker-diagnosis ladder. <c>Resolver</c> is intentionally
    /// absent — the acting identity is derived from the authenticated caller (I2), never
    /// trusted from the client.
    /// </summary>
    public sealed record BlockerResolutionRequest(
        Guid SessionId,
        string Kind,              // "progress" | "escalation"
        string? Level,            // required for progress: Hint | Guidance | Assistance
        bool? Resolved,           // escalation: did the senior resolve it (default true)
        string? ProgressType,     // progress: commit | ci | manual | ...
        string? Details,          // progress: free-text detail
        string? SeniorResponse);  // escalation: senior's response note

    /// <summary>
    /// Resume the blocker-diagnosis progressive-resolution ladder for a mentorship session.
    /// Route: <c>POST /api/adl/blocker/resume</c>.
    /// </summary>
    public static async Task<IResult> ResumeBlocker(
        [FromBody] BlockerResolutionRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] IMentorshipService mentorship,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.AdlEndpoints");

        if (req.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(req.Kind) || !AllowedBlockerKinds.Contains(req.Kind.Trim()))
            return Results.BadRequest(new { error = "kind must be one of: progress, escalation" });

        var kind = req.Kind.Trim().ToLowerInvariant();
        string? level = null;
        if (kind == "progress")
        {
            if (string.IsNullOrWhiteSpace(req.Level) || !AllowedBlockerLevels.Contains(req.Level.Trim()))
                return Results.BadRequest(new { error = "level must be one of: Hint, Guidance, Assistance (for kind=progress)" });
            level = CanonicalBlockerLevel(req.Level);
        }

        // I2 — the resolver is the authenticated caller, derived server-side. A
        // client-supplied resolver is never honoured so the audit trail can't be forged.
        var resolver = ResolveApprover(principal);

        try
        {
            // IDOR guard (mirrors the merge/deploy "cross-tenant → 404, never acts"):
            // GetSessionAsync is tenant-scoped (per-tenant schema), so a session that does
            // not belong to the caller's ambient tenant simply resolves to null. We refuse
            // to forward a resume for a session the caller does not own — the resume can only
            // ever target the caller's OWN blocker gate.
            var session = await mentorship.GetSessionAsync(req.SessionId);
            if (session is null)
            {
                logger.LogWarning(
                    "Blocker resume for session {SessionId} not found in caller tenant {Tenant} — refusing",
                    req.SessionId, tenantContext.TenantId);
                return Results.NotFound(new
                {
                    error = "session_not_found",
                    detail = "No blocker-diagnosis session with this id exists in your scope.",
                });
            }

            var result = await elsa.ResumeBlockerResolutionAsync(
                req.SessionId, kind, level, req.Resolved ?? true,
                req.ProgressType, req.Details, req.SeniorResponse, resolver);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No blocker-diagnosis wait is currently suspended for this session/level.",
                });
            }

            logger.LogInformation(
                "Drove blocker gate for session {SessionId} (kind {Kind}, level {Level}, resolver {Resolver})",
                req.SessionId, LogSanitizer.Clean(kind), LogSanitizer.Clean(level ?? ""), LogSanitizer.Clean(resolver));

            return Results.Ok(new
            {
                resumed = result.Resumed,
                workflowInstanceId = result.WorkflowInstanceId,
                kind,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resume blocker gate for session {SessionId}", req.SessionId);
            return Results.Problem(
                detail: "Failed to resume the blocker-diagnosis gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ================================================================
    // Clarifying-questions human-answer gate (Story 3.5) —
    // POST /api/adl/clarify/resume. Same RBAC (WorkflowsManage, enforced at
    // the route group) + I2 (server-derived resolver) posture as the merge/
    // deploy gates. The cross-tenant guard uses the SAME mechanism as the merge
    // gate: the caller's ambient tenant id is folded into the bookmark name by
    // the engine, so a caller can only resume a gate in its OWN tenant (a
    // cross-tenant/unknown session → 404). This is table-free — Story 3.5 mints
    // no clarify-session row (NON-MIGRATION).
    // ================================================================

    /// <summary>
    /// Resume request for the clarifying-questions answer gate. <c>Resolver</c> is
    /// intentionally absent — the acting identity is derived from the authenticated
    /// caller (I2), never trusted from the client.
    /// </summary>
    public sealed record ClarifyAnswersRequest(
        Guid SessionId,
        string Answers);

    /// <summary>
    /// Resume the clarifying-questions workflow with a stakeholder's answers.
    /// Route: <c>POST /api/adl/clarify/resume</c>.
    /// </summary>
    public static async Task<IResult> ResumeClarify(
        [FromBody] ClarifyAnswersRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.AdlEndpoints");

        if (req.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(req.Answers))
            return Results.BadRequest(new { error = "answers is required" });

        // SECURITY (IDOR) — scope the resume to the caller's ambient tenant. The engine
        // folds this tenant id into the bookmark name, so a caller can only ever resolve a
        // gate in its OWN tenant. (Ambient null = self-hosted single-user scope.)
        var tenantId = tenantContext.TenantId?.ToString();

        // I2 — the resolver is the authenticated caller, derived server-side. A
        // client-supplied resolver is never honoured so the audit trail can't be forged.
        var resolver = ResolveApprover(principal);

        try
        {
            var result = await elsa.ResumeClarifyingQuestionsAsync(
                req.SessionId, tenantId, req.Answers, resolver);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No clarifying-questions wait is currently suspended for this session.",
                });
            }

            logger.LogInformation(
                "Drove clarify gate for session {SessionId} (resolver {Resolver})",
                req.SessionId, LogSanitizer.Clean(resolver));

            return Results.Ok(new
            {
                resumed = result.Resumed,
                workflowInstanceId = result.WorkflowInstanceId,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resume clarify gate for session {SessionId}", req.SessionId);
            return Results.Problem(
                detail: "Failed to resume the clarifying-questions gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ================================================================
    // Design-proposal human review gate (Story 3.7) —
    // POST /api/adl/design/resume. Same RBAC (WorkflowsManage, enforced at the
    // route group) + I2 (server-derived reviewer) posture as the merge/deploy
    // gates. The cross-tenant guard uses the SAME mechanism as the merge gate:
    // the caller's ambient tenant id is folded into the bookmark name by the
    // engine, so a caller can only resume a gate in its OWN tenant (a cross-
    // tenant/unknown session → 404). This is table-free — Story 3.7 mints no
    // design-proposal row (NON-MIGRATION).
    // ================================================================

    /// <summary>The design review decisions accepted (case-insensitive). Anything else is
    /// rejected with 400 up front so the caller never forwards an arbitrary string that
    /// would silently route the gate.</summary>
    private static readonly HashSet<string> AllowedDesignDecisions =
        new(StringComparer.OrdinalIgnoreCase) { "approve", "reject" };

    /// <summary>
    /// Resume request for the design-proposal review gate. <c>Reviewer</c> is intentionally
    /// absent — the acting identity is derived from the authenticated caller (I2), never
    /// trusted from the client.
    /// </summary>
    public sealed record DesignReviewRequest(
        Guid SessionId,
        string Decision,       // "approve" | "reject"
        string? Feedback);

    /// <summary>
    /// Resume the design-proposal workflow with a reviewer's approve/reject decision.
    /// Route: <c>POST /api/adl/design/resume</c>.
    /// </summary>
    public static async Task<IResult> ResumeDesign(
        [FromBody] DesignReviewRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.AdlEndpoints");

        if (req.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });
        if (string.IsNullOrWhiteSpace(req.Decision) || !AllowedDesignDecisions.Contains(req.Decision.Trim()))
            return Results.BadRequest(new { error = "decision must be one of: approve, reject" });

        var approved = string.Equals(req.Decision.Trim(), "approve", StringComparison.OrdinalIgnoreCase);

        // SECURITY (IDOR) — scope the resume to the caller's ambient tenant. The engine folds
        // this tenant id into the bookmark name, so a caller can only ever resolve a gate in
        // its OWN tenant. (Ambient null = self-hosted single-user scope.)
        var tenantId = tenantContext.TenantId?.ToString();

        // I2 — the reviewer is the authenticated caller, derived server-side. A client-supplied
        // reviewer is never honoured so the audit trail can't be forged.
        var reviewer = ResolveApprover(principal);

        try
        {
            var result = await elsa.ResumeDesignApprovalAsync(
                req.SessionId, tenantId, approved, req.Feedback, reviewer);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No design-proposal review is currently suspended for this session.",
                });
            }

            logger.LogInformation(
                "Drove design gate for session {SessionId} with decision {Decision} (reviewer {Reviewer})",
                req.SessionId, LogSanitizer.Clean(req.Decision), LogSanitizer.Clean(reviewer));

            return Results.Ok(new
            {
                resumed = result.Resumed,
                workflowInstanceId = result.WorkflowInstanceId,
                approved,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resume design gate for session {SessionId}", req.SessionId);
            return Results.Problem(
                detail: "Failed to resume the design-proposal review gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Map a case-insensitive level onto the workflow's exact PascalCase segment
    /// ("Hint"/"Guidance"/"Assistance"); null for anything else.</summary>
    internal static string? CanonicalBlockerLevel(string? level)
        => level?.Trim().ToLowerInvariant() switch
        {
            "hint" => "Hint",
            "guidance" => "Guidance",
            "assistance" => "Assistance",
            _ => null,
        };

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
