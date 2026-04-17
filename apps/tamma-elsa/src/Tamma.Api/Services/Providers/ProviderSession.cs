namespace Tamma.Api.Services.Providers;

/// <summary>
/// Lifecycle metadata for an active provider session.
/// </summary>
/// <param name="Handle">Opaque session identifier (UUID string).</param>
/// <param name="Provider">Provider key (e.g. <c>anthropic</c>, <c>openai</c>).</param>
/// <param name="Model">Model identifier (e.g. <c>claude-sonnet-4</c>).</param>
/// <param name="CreatedAt">UTC instant the session was created.</param>
/// <param name="LastUsed">
/// UTC instant of the most recent <see cref="IProviderSessionService.GetAsync"/>
/// or <see cref="IProviderSessionService.ExecuteAsync"/> hit. Used for TTL
/// eviction by the cleanup hosted service.
/// </param>
/// <param name="TenantId">
/// Optional owning tenant. Only the owner may execute / delete the session.
/// <c>null</c> denotes a global/system session (not tenant-isolated).
/// </param>
public sealed record ProviderSession(
    string Handle,
    string Provider,
    string Model,
    DateTime CreatedAt,
    DateTime LastUsed,
    Guid? TenantId);

/// <summary>
/// Request body for <c>POST /api/providers/providers/{handle}/execute</c>.
/// </summary>
/// <param name="Handle">Session handle from <see cref="IProviderSessionService.CreateAsync"/>.</param>
/// <param name="Input">Prompt or instruction text sent to the provider.</param>
/// <param name="MaxTokens">Optional ceiling on response tokens.</param>
/// <param name="Temperature">Optional sampling temperature override.</param>
public sealed record ExecuteRequest(
    string Handle,
    string Input,
    int? MaxTokens,
    double? Temperature);

/// <summary>
/// Normalised result of a provider invocation, used for both the HTTP
/// response body and the diagnostic recording pipeline.
/// </summary>
/// <param name="Content">Textual output from the provider.</param>
/// <param name="TokenUsage">Total tokens consumed (prompt + completion).</param>
/// <param name="CostUsd">Estimated cost in USD for this invocation.</param>
/// <param name="DurationMs">Wall-clock latency measured by the service.</param>
public sealed record ExecuteResult(
    string Content,
    int TokenUsage,
    decimal CostUsd,
    long DurationMs);

/// <summary>
/// Raw invocation result returned by an <see cref="IProviderClient"/>. The
/// service layer wraps this in <see cref="ExecuteResult"/> after adding
/// timing / diagnostic side effects.
/// </summary>
public sealed record ProviderInvocationResult(
    string Content,
    int TokensUsed,
    decimal CostUsd,
    long DurationMs);

/// <summary>
/// Thrown when a handle does not resolve to a session (or resolves to a
/// session owned by a different tenant than the caller).
/// </summary>
public sealed class ProviderSessionNotFoundException : Exception
{
    public ProviderSessionNotFoundException(string handle)
        : base($"Provider session not found: {handle}") { }
}
