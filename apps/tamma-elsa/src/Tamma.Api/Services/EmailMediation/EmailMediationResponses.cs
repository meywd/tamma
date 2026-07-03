using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// Story 38 (Phase 1) — the single normalized, KEY-FREE result the email endpoint
/// returns. No recipient / body / credential ever appears here. The mirroring
/// engine-side wire type is
/// <c>Tamma.Activities.LlmCall.Models.EmailCallResponse</c>.
/// </summary>
public sealed record EmailMediationResult
{
    public bool Success { get; init; }

    /// <summary>"Queued" on accept (the outbox owns transport), else "Error".</summary>
    public string? Outcome { get; init; }

    /// <summary>The transaction id the outbox-backed IEmailService returns — the
    /// correlation key for the later EMAIL.SENT.* / EMAIL.SENT.FAILED events.</summary>
    public Guid? TxnId { get; init; }

    // ── failure-only (key-free) ──
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }

    public string? CorrelationId { get; init; }
}

/// <summary>
/// Story 38 (Phase 1) — the coarse, key-free email failure taxonomy. Never a raw
/// provider 5xx.
/// </summary>
public static class EmailMediationFailureCodes
{
    /// <summary>Any expected accept failure (validation, DI, transient).</summary>
    public const string PlatformError = "PLATFORM_ERROR";

    /// <summary>
    /// Per-tenant BYOK fail-loud: no email transport credential resolved for the
    /// acting tenant (no tenant cabinet bundle in SaaS, and no single-user
    /// <c>Email:*</c> config). The transport is NEVER reached — the mediation
    /// fails loud rather than sending under a shared platform sender identity
    /// (the confused-deputy). The tenant registers its own credential via
    /// <c>POST /api/v1/integrations/email/credential</c>.
    /// </summary>
    public const string CredentialUnavailable = "EMAIL_CREDENTIAL_UNAVAILABLE";
}

/// <summary>
/// Story 38 (Phase 1) — the HTTP-status decision. Email is fail-soft (a missing
/// notification must not break a workflow), so a failure rides inside a 200
/// success:false envelope. A raw 5xx is NEVER produced.
/// </summary>
public static class EmailMediationResultExtensions
{
    public static IResult ToHttpResult(this EmailMediationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Results.Ok(result);
    }
}
