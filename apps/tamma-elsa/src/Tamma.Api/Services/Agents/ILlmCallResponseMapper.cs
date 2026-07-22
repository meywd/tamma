using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (AC2/AC7/AC10) — projects the internal <see cref="AgentRunResult"/>
/// to the wire <see cref="LlmCallResponse"/> AND to the §2.4 HTTP result the
/// endpoint (T4) returns. Owned by T3 (the projection + status logic); T4 wires
/// <see cref="ToHttpResult"/> into the endpoint.
///
/// <para><b>The load-bearing status discipline (AC7):</b></para>
/// <list type="bullet">
///   <item><description>Success ⇒ HTTP 200.</description></item>
///   <item><description>Expected execution failure (<c>PROVIDER_ERROR</c>,
///     <c>PROVIDER_CREDENTIAL_UNAVAILABLE</c>, <c>BUDGET_EXCEEDED</c>,
///     <c>LOOP_EXHAUSTED</c>) ⇒ HTTP 200 + <c>success:false</c>, with
///     <see cref="LlmCallResponse.HttpStatusCode"/> PRESERVED so the engine's
///     <c>RetryCheck</c> + circuit breaker keep working.</description></item>
///   <item><description><b>Finding C-1</b> — gate denials are TERMINAL and must
///     not be retried, but the only caller is the engine via
///     <c>TammaApiClient.PostAsync</c>, which NULLS any non-2xx body. So a gate
///     denial rides inside HTTP 200 + <c>success:false</c> too, carrying a
///     NON-transient <see cref="LlmCallResponse.HttpStatusCode"/> in the BODY:
///     <c>400</c> for <c>SAAS_PROVIDER_NOT_ALLOWED</c>, <c>403</c> for
///     <c>TENANT_NOT_ENTITLED</c>. Neither value is in <c>RetryCheck</c>'s
///     transient set <c>{0,429,502,503,504}</c>, so the engine receives a real
///     body and STOPS.</description></item>
/// </list>
/// <para>NEVER a raw non-2xx — any non-2xx is nulled by
/// <c>TammaApiClient.PostAsync</c> and would silently break the retry / breaker
/// boundary (Finding C-1).</para>
/// </summary>
public interface ILlmCallResponseMapper
{
    /// <summary>Project the producer record to the key-free wire response
    /// (every field carried forward; cost populated; <c>credentialSource</c>
    /// copied). Used on every path — success and failure.</summary>
    LlmCallResponse ToResponse(AgentRunResult run);

    /// <summary>Map the producer record to the §2.4 HTTP result. Every outcome is
    /// HTTP 200: success / success:false with the (preserved or stamped)
    /// non-transient httpStatusCode in the body — gate denials carry 400/403 in the
    /// body (Finding C-1), never as the HTTP status. Never a raw non-2xx.</summary>
    IResult ToHttpResult(AgentRunResult run);
}

/// <summary>
/// Story 32-5 — the failure-code constants <c>ManagedAgent</c> stamps onto an
/// <see cref="AgentRunResult.FailureCode"/> and the mapper branches on. Kept as
/// the single source so composition and the mapper never drift.
/// </summary>
public static class AgentRunFailureCodes
{
    /// <summary>An upstream provider call failed (preserves the HTTP status).</summary>
    public const string ProviderError = "PROVIDER_ERROR";

    /// <summary>No usable credential (provider never called; retryable:false).</summary>
    public const string CredentialUnavailable = "PROVIDER_CREDENTIAL_UNAVAILABLE";

    /// <summary>The agent/prompt could not be resolved — no enabled default, an
    /// unresolved custom-agent prompt, or an unknown role. This is a CONFIG /
    /// VALIDATION failure (no provider call happens), NOT a credential or provider
    /// problem; retryable:false. The mapper rides it inside a 200 envelope with an
    /// httpStatusCode (422) the engine's RetryCheck will NOT retry.</summary>
    public const string AgentUnresolved = "AGENT_UNRESOLVED";

    /// <summary>Over budget / budget could not be evaluated (loop never invoked).</summary>
    public const string BudgetExceeded = "BUDGET_EXCEEDED";

    /// <summary>The tool loop exhausted maxSteps with no usable response.</summary>
    public const string LoopExhausted = "LOOP_EXHAUSTED";

    /// <summary>Story 39-9 (D5) — the produced document still failed its deterministic
    /// validator after the repair ring was exhausted. A CONTENT failure (the provider
    /// worked; the output is wrong — think 422 vs 503), NOT a transport/provider
    /// problem: the mapper stamps body <c>httpStatusCode</c> 422 (NOT in RetryCheck's
    /// transient set), and the diagnostic path excludes it from the circuit breaker.
    /// The final violations + per-turn history ride on the result for the 39-6
    /// <c>ValidationExhausted</c> lineage.</summary>
    public const string ContentValidationFailed = "CONTENT_VALIDATION_FAILED";

    /// <summary>A SaaS gate denial of a CLI-token / unknown provider ⇒ 400.</summary>
    public const string SaasProviderNotAllowed = "SAAS_PROVIDER_NOT_ALLOWED";

    /// <summary>A SaaS entitlement rejection of the tenant ⇒ 403.</summary>
    public const string TenantNotEntitled = "TENANT_NOT_ENTITLED";
}
