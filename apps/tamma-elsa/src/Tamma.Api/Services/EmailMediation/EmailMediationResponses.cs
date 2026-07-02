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
    /// SaaS fail-closed guard: a tenant-workflow-initiated mediated send was denied
    /// because the message would go out FROM the platform's configured sender
    /// identity (<c>Email:From</c>) with no per-tenant sender/domain allowlist. Opt
    /// back in with <c>Email:AllowMediatedSendInSaaS=true</c> once a per-tenant sender
    /// policy exists. Single-user mode is never denied (one principal owns the domain).
    /// </summary>
    public const string MediationDeniedInSaaS = "email_mediation_denied_in_saas";
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
