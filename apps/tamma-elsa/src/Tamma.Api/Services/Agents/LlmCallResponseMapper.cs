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
        };
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

        // Gate denials are the ONLY non-200 outcomes — everything else is an
        // expected EXECUTION failure that MUST ride inside a 200 envelope so the
        // engine's RetryCheck/circuit-breaker keep working (a raw 5xx is nulled
        // by TammaApiClient.PostAsync). NEVER a 5xx.
        return run.FailureCode switch
        {
            AgentRunFailureCodes.SaasProviderNotAllowed => Results.Json(body, statusCode: 400),
            AgentRunFailureCodes.TenantNotEntitled => Results.Json(body, statusCode: 403),
            // PROVIDER_ERROR / PROVIDER_CREDENTIAL_UNAVAILABLE / BUDGET_EXCEEDED /
            // LOOP_EXHAUSTED / anything else ⇒ 200 success:false (+ preserved
            // httpStatusCode inside the body).
            _ => Results.Ok(body),
        };
    }
}
