namespace Tamma.Api.Services.SaaS;

/// <summary>A single message in a chat conversation.</summary>
/// <param name="Role"><c>system</c>, <c>user</c>, or <c>assistant</c>.</param>
/// <param name="Content">Message text.</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>Inbound chat request for the SaaS LLM proxy.</summary>
/// <param name="Model">Upstream model identifier (e.g. <c>claude-sonnet-4.5</c>). Optional — a default is chosen when null.</param>
/// <param name="Messages">Ordered list of conversation turns.</param>
/// <param name="MaxTokens">Optional cap on completion tokens.</param>
/// <param name="Temperature">Optional sampling temperature.</param>
public sealed record ChatRequest(
    string? Model,
    IReadOnlyList<ChatMessage> Messages,
    int? MaxTokens,
    double? Temperature);

/// <summary>Result of a successful (or failed) LLM chat call.</summary>
/// <param name="Success">True when the upstream returned a parseable response.</param>
/// <param name="Text">Concatenated assistant text, or null on error.</param>
/// <param name="Model">Model id echoed back by the provider.</param>
/// <param name="PromptTokens">Input token count (diagnostic fidelity).</param>
/// <param name="CompletionTokens">Output token count.</param>
/// <param name="TotalTokens">Sum of the above.</param>
/// <param name="CostUsd">Estimated USD cost from the pricing table.</param>
/// <param name="ErrorReason">Short machine-readable reason when <see cref="Success"/> is false.</param>
public sealed record ChatResponse(
    bool Success,
    string? Text,
    string? Model,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    decimal CostUsd,
    string? ErrorReason);

/// <summary>
/// Proxies chat requests to the configured LLM provider and records per-call
/// diagnostics (tokens + cost) for the owning tenant. Refuses requests when
/// the tenant's budget is exhausted.
/// </summary>
/// <remarks>
/// Currently hard-coded to Anthropic; future work will plug in the full
/// multi-provider chain via <c>IAgentProvider</c>. The contract is intentionally
/// small so the endpoint can be smoke-tested end-to-end without touching the
/// provider chain.
/// </remarks>
public interface ILlmProxyService
{
    /// <summary>Send a chat request and record the outcome to diagnostics.</summary>
    Task<ChatResponse> ChatAsync(ChatRequest request, Guid? tenantId, CancellationToken ct = default);
}
