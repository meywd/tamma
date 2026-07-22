using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (AC2/AC7/AC10) — the <see cref="AgentRunResult"/> →
/// <see cref="LlmCallResponse"/> projection + the §2.4 HTTP-status decision.
/// See <see cref="ILlmCallResponseMapper"/> for the status discipline. Pure /
/// stateless — safe as a singleton.
/// </summary>
public sealed class LlmCallResponseMapper : ILlmCallResponseMapper
{
    /// <inheritdoc />
    public LlmCallResponse ToResponse(AgentRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new LlmCallResponse
        {
            Success = run.Success,
            Text = run.ResponseText,
            Usage = new UsageDto
            {
                PromptTokens = run.InputTokens,
                CompletionTokens = run.OutputTokens,
                TotalTokens = run.InputTokens + run.OutputTokens,
                ToolLoopTokens = run.ToolLoopTokens,
                ToolLoopTurns = run.ToolLoopTurns,
                ToolLoopExhausted = run.ToolLoopExhausted,
            },
            CredentialSource = run.CredentialSource,
            ProviderUsed = string.IsNullOrEmpty(run.Provider) ? null : run.Provider,
            ModelUsed = string.IsNullOrEmpty(run.Model) ? null : run.Model,
            Cost = new CostDto
            {
                ProviderCostUsd = run.CostUsd,
                PriceUsd = run.PriceUsd,
                Currency = "USD",
            },
            ToolCalls = run.ToolCalls,
            AgentId = run.AgentId,
            AgentVersion = run.Version,
            Role = string.IsNullOrEmpty(run.Role) ? null : run.Role,
            CorrelationId = run.CorrelationId,
            DurationMs = run.DurationMs,
            FailureCode = run.FailureCode,
            FailureReason = run.FailureReason,
            HttpStatusCode = run.HttpStatusCode,
            ContentValidation = ToContentValidationDto(run),
        };
    }

    /// <summary>Story 39-9 — project the repair-ring outcome onto the wire block.
    /// <c>null</c> when no validator applied (<see cref="AgentRunResult.ContentValid"/>
    /// is <c>null</c>) ⇒ additive, zero change for existing dispatchers.</summary>
    private static ContentValidationDto? ToContentValidationDto(AgentRunResult run)
    {
        if (run.ContentValid is null)
        {
            return null;
        }

        var finalViolations = (run.ContentViolations ?? Array.Empty<Tamma.Core.Documents.DocumentViolation>())
            .Select(v => new ContentViolationDto(v.Code, v.Message))
            .ToList();

        var history = (run.RepairHistory ?? Array.Empty<RepairTurnRecord>())
            .Select(t => new RepairTurnDto(
                t.Turn,
                t.Valid,
                t.Violations.Select(v => new ContentViolationDto(v.Code, v.Message)).ToList()))
            .ToList();

        return new ContentValidationDto(run.ContentValid.Value, run.RepairTurns, finalViolations, history);
    }

    /// <inheritdoc />
    public IResult ToHttpResult(AgentRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var body = ToResponse(run);

        // Success ⇒ 200.
        if (run.Success)
        {
            return Results.Ok(body);
        }

        // Finding C-1 — EVERY failure (gate denials included) rides inside a 200
        // envelope. The endpoint's ONLY caller is the engine via
        // TammaApiClient.PostAsync, which returns null for any non-2xx response.
        // A gate denial returned as a raw HTTP 400/403 would therefore be nulled →
        // the shim would write httpStatusCode 0 (transient) → RetryCheck would
        // RETRY a TERMINAL denial. So gate denials are encoded the §2.4 way:
        // HTTP 200 + success:false + a NON-transient httpStatusCode carried in the
        // BODY (400 for SAAS_PROVIDER_NOT_ALLOWED, 403 for TENANT_NOT_ENTITLED) —
        // neither value is in RetryCheck's transient set {0, 429, 502, 503, 504},
        // so the engine receives a real body and STOPS. NEVER a raw 4xx/5xx.
        return run.FailureCode switch
        {
            // Gate denials — terminal, non-retryable. Body status 400/403 stamped
            // if the producer left it null (both are non-transient ⇒ RetryCheck stops).
            AgentRunFailureCodes.SaasProviderNotAllowed =>
                Results.Ok(WithBodyStatus(body, body.HttpStatusCode ?? 400)),
            AgentRunFailureCodes.TenantNotEntitled =>
                Results.Ok(WithBodyStatus(body, body.HttpStatusCode ?? 403)),
            // AGENT_UNRESOLVED (config/validation: no enabled default, unresolved
            // custom prompt, unknown role) is engine-internal — never a raw 4xx/5xx
            // on the wire. It rides inside a 200 success:false envelope, but the
            // body carries an httpStatusCode of 422 (Unprocessable Entity), which
            // is NOT in RetryCheck's transient set {0, 429, 502, 503, 504}, so the
            // engine will NOT retry a config failure against the same provider.
            AgentRunFailureCodes.AgentUnresolved => Results.Ok(WithBodyStatus(body, 422)),
            // Story 39-9 (D5) — CONTENT_VALIDATION_FAILED is a CONTENT failure (the
            // provider worked; the document is wrong). It rides a 200 success:false
            // envelope with a body httpStatusCode of 422 (Unprocessable Entity), which
            // is NOT in RetryCheck's transient set {0, 429, 502, 503, 504}, so the
            // provider chain does NOT retry it — the lifecycle maps it to
            // ValidationExhausted. (The breaker exclusion is enforced engine-side.)
            AgentRunFailureCodes.ContentValidationFailed =>
                Results.Ok(WithBodyStatus(body, body.HttpStatusCode ?? 422)),
            // PROVIDER_ERROR / PROVIDER_CREDENTIAL_UNAVAILABLE / BUDGET_EXCEEDED /
            // LOOP_EXHAUSTED / anything else ⇒ 200 success:false (+ preserved
            // httpStatusCode inside the body).
            _ => Results.Ok(body),
        };
    }

    /// <summary>Return a copy of <paramref name="body"/> with its
    /// <see cref="LlmCallResponse.HttpStatusCode"/> set to <paramref name="status"/>.
    /// Used to stamp the non-retryable 422 on an AGENT_UNRESOLVED body even when the
    /// producer left it null — the wire envelope stays 200 success:false.</summary>
    private static LlmCallResponse WithBodyStatus(LlmCallResponse body, int status)
        => body with { HttpStatusCode = status };
}
