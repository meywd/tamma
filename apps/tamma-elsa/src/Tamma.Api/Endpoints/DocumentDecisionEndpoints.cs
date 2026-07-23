using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Services;
using Tamma.Api.Services.Documents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Logging;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 39-8 (AC5) — the RBAC-gated PUBLIC surface for the ONE generic document-decision
/// gate and the escalation disposition surface. Mirrors <see cref="AdlEndpoints"/>'s
/// server-derives-everything posture: the tenant, decider, and channel are derived from the
/// authenticated principal SERVER-SIDE (D6/D7) — never trusted from the client body — and the
/// decision <c>kind</c> is validated against the closed 39-5 set before anything forwards.
///
/// <para>The public endpoint builds the 39-5 <see cref="AcceptanceDecision"/> JSON server-side
/// from a small typed request, then forwards to the engine's in-process resume seam
/// (<c>POST /elsa/api/documents/decision/resume</c>) which folds the caller's ambient tenant
/// id into the bookmark name (cross-tenant → 404).</para>
/// </summary>
public static class DocumentDecisionEndpoints
{
    /// <summary>
    /// Public decision request. The decider + channel are intentionally ABSENT — they are
    /// derived from the authenticated principal (D6/D7), never trusted from the client.
    /// </summary>
    public sealed record DecisionRequest(
        string Kind,        // accept | request-revision | reject | escalate
        string? Notes,      // request-revision notes
        string? Reason,     // reject reason / escalate reason wire
        string? Detail,     // escalate detail
        string? Feedback);  // free-text feedback captured on the trail

    /// <summary>Public disposition request for an escalation.</summary>
    public sealed record DispositionRequest(
        string Disposition, // resolved | overridden | abandoned
        string? Note);

    // ================================================================
    // POST /api/documents/decisions/{sessionId}/resume
    // ================================================================

    public static async Task<IResult> ResumeDecision(
        Guid sessionId,
        [FromBody] DecisionRequest req,
        [FromServices] IElsaWorkflowService elsa,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.DocumentDecisionEndpoints");

        if (sessionId == Guid.Empty)
            return Results.BadRequest(new { error = "sessionId is required" });
        if (!DocumentDecisionSubmissionService.IsAllowedKind(req.Kind))
            return Results.BadRequest(new { error = "kind must be one of: accept, request-revision, reject, escalate" });

        // SECURITY (IDOR) — scope the resume to the caller's ambient tenant; the engine folds it
        // into the bookmark name so a caller can only resolve a gate in its OWN tenant.
        var tenantId = tenantContext.TenantId;

        // D7 — decider derived server-side (non-repudiation). D6 — channel derived from the
        // authenticated principal, NEVER read from the body. Both the build + the resume live in
        // the shared DocumentDecisionSubmissionService so the hub (39-18) drives the SAME path.
        var deciderId = ResolveApprover(principal);
        var channel = ApprovalChannels.Derive(principal);
        var submission = new DocumentDecisionSubmissionService(elsa);

        try
        {
            var result = await submission.SubmitAsync(sessionId, req, tenantId, deciderId, channel);

            if (result.GateNotFound)
            {
                return Results.NotFound(new
                {
                    error = "gate_not_waiting",
                    detail = "No document decision is currently suspended for this session.",
                });
            }

            logger.LogInformation(
                "Drove document-decision gate for session {SessionId} kind {Kind} channel {Channel} (decider {Decider})",
                sessionId, LogSanitizer.Clean(req.Kind), channel.ToWire(), LogSanitizer.Clean(deciderId));

            return Results.Ok(new
            {
                resumed = result.Resumed,
                workflowInstanceId = result.WorkflowInstanceId,
                kind = req.Kind.Trim().ToLowerInvariant(),
                channel = channel.ToWire(),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume document-decision gate for session {SessionId}", sessionId);
            return Results.Problem(
                detail: "Failed to resume the document-decision gate.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ================================================================
    // POST /api/documents/escalations/{escalationId}/resolve
    // ================================================================

    public static async Task<IResult> ResolveEscalation(
        string escalationId,
        [FromBody] DispositionRequest req,
        [FromServices] EscalationDispositionService disposition,
        [FromServices] ITenantContext tenantContext,
        ClaimsPrincipal principal,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.DocumentDecisionEndpoints");

        if (string.IsNullOrWhiteSpace(escalationId))
            return Results.BadRequest(new { error = "escalationId is required" });

        EscalationDisposition parsed;
        try
        {
            parsed = EscalationDispositionExtensions.Parse(req.Disposition);
        }
        catch (Tamma.Core.TammaError)
        {
            return Results.BadRequest(new { error = "disposition must be one of: resolved, overridden, abandoned" });
        }

        var tenantId = tenantContext.TenantId;
        var deciderId = ResolveApprover(principal);
        var channel = ApprovalChannels.Derive(principal);

        var result = await disposition.DispositionAsync(
            tenantId, escalationId, parsed, req.Note, deciderId, channel);

        switch (result.Outcome)
        {
            case EscalationDispositionOutcome.NotFound:
                return Results.NotFound(new
                {
                    error = "escalation_not_found",
                    detail = "No triggered escalation exists for this escalationId.",
                });
            case EscalationDispositionOutcome.AlreadyResolved:
                return Results.Conflict(new
                {
                    error = "escalation_already_resolved",
                    detail = "This escalation has already been dispositioned.",
                });
            default:
                logger.LogInformation(
                    "Dispositioned escalation {EscalationId} as {Disposition} (channel {Channel}, durationMs {Duration})",
                    LogSanitizer.Clean(escalationId), parsed.ToWire(), channel.ToWire(), result.DurationMs);
                return Results.Ok(new
                {
                    resolved = true,
                    disposition = parsed.ToWire(),
                    channel = channel.ToWire(),
                    durationMs = result.DurationMs,
                });
        }
    }

    // ================================================================
    // Server-side derivations (copied from AdlEndpoints — I2)
    // ================================================================

    /// <summary>
    /// Derive the decider identity from the authenticated principal (I2):
    /// email → display name → subject id. Falls back to <c>"unknown"</c> only if the principal
    /// carries none of these (it should never, behind auth). Copied from
    /// <see cref="AdlEndpoints.ResolveApprover"/> per the extraction convention.
    /// </summary>
    internal static string ResolveApprover(ClaimsPrincipal principal)
        => principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
           ?? principal.FindFirst(ClaimTypes.Email)?.Value
           ?? principal.FindFirst("name")?.Value
           ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
           ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? "unknown";
}

/// <summary>
/// Story 39-8 (D6) — the pure derivation of the transport <see cref="ApprovalChannel"/> from
/// the authenticated principal. There is exactly one rule, expressed as data (never a mode
/// branch): a principal carrying the orchestrator service claim →
/// <see cref="ApprovalChannel.Orchestrator"/>; a non-interactive service credential (API-key
/// auth scheme, or an explicit <c>api</c>/<c>service</c> principal type) →
/// <see cref="ApprovalChannel.Api"/>; any other authenticated human session →
/// <see cref="ApprovalChannel.User"/>. NEVER read the channel from the request body.
///
/// <para>The orchestrator claim (<c>tamma:principal-type = orchestrator</c>) is minted by the
/// 39-17 host when it lands; the constant is DEFINED HERE and 39-17/39-18 adopt it. Until then
/// tests mint the claim directly.</para>
/// </summary>
public static class ApprovalChannels
{
    /// <summary>The claim key carrying a caller's Tamma principal type.</summary>
    public const string PrincipalTypeClaim = "tamma:principal-type";

    /// <summary>The principal-type value marking the orchestrator agent (39-17).</summary>
    public const string OrchestratorPrincipalType = "orchestrator";

    public static ApprovalChannel Derive(ClaimsPrincipal principal)
    {
        var principalType = principal.FindFirst(PrincipalTypeClaim)?.Value;

        if (string.Equals(principalType, OrchestratorPrincipalType, StringComparison.OrdinalIgnoreCase))
            return ApprovalChannel.Orchestrator;

        if (IsServiceCredential(principal, principalType))
            return ApprovalChannel.Api;

        return ApprovalChannel.User;
    }

    private static bool IsServiceCredential(ClaimsPrincipal principal, string? principalType)
    {
        if (string.Equals(principalType, "api", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(principalType, "service", StringComparison.OrdinalIgnoreCase))
            return true;

        // The ApiKey auth scheme stamps AuthenticationType = "ApiKey" on the identity — a
        // non-interactive programmatic caller.
        return principal.Identities.Any(i =>
            string.Equals(i.AuthenticationType, "ApiKey", StringComparison.OrdinalIgnoreCase));
    }
}
