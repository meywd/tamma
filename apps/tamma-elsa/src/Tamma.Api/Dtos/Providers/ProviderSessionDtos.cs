namespace Tamma.Api.Dtos.Providers;

/// <summary>
/// Request body for <c>POST /api/providers/providers/create</c>. Matches the
/// Story 9-4 TypeScript <c>CreateSessionInput</c> shape so the TS engine
/// and Elsa workflows can keep calling with the same payload.
/// </summary>
/// <param name="Provider">Provider key (<c>anthropic</c>, <c>openai</c>, …).</param>
/// <param name="Model">Optional model identifier. Defaults to <c>default</c>.</param>
/// <param name="ApiKeyRef">
/// Optional identifier of the credential record to use. Reserved for future
/// KMS/Vault integration; currently ignored by the session service.
/// </param>
public sealed record CreateProviderSessionRequest(
    string Provider,
    string? Model,
    string? ApiKeyRef);

/// <summary>Response body for <c>POST /api/providers/providers/create</c>.</summary>
public sealed record CreateProviderSessionResponse(
    string Handle,
    string Provider,
    string Model);

/// <summary>
/// Request body for <c>POST /api/providers/providers/{handle}/execute</c>.
/// Accepts either <c>prompt</c> (TS convention) or <c>input</c> (new) so
/// existing callers don't need to be patched.
/// </summary>
public sealed record ExecuteProviderSessionRequest(
    string? Prompt,
    string? Input,
    int? MaxTokens,
    double? Temperature);

/// <summary>Response body for the execute endpoint.</summary>
public sealed record ExecuteProviderSessionResponse(
    string Content,
    int TokenUsage,
    decimal CostUsd,
    long DurationMs);

/// <summary>Response entry for <c>GET /api/providers/providers/sessions</c>.</summary>
public sealed record ProviderSessionDto(
    string Handle,
    string Provider,
    string Model,
    DateTime CreatedAt,
    DateTime LastUsed,
    Guid? TenantId);
