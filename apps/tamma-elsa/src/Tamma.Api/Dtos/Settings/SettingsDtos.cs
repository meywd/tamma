namespace Tamma.Api.Dtos.Settings;

public record UpdateAgentsConfigRequest(object Config);
public record UpdateSecurityConfigRequest(object Config);
public record UpdateSanitizationRulesRequest(object Rules);

/// <summary>
/// Legacy name for the sanitize endpoint payload. Preserved for binary-compat
/// with callers compiled before the sanitization rewrite. New callers should
/// use <see cref="SanitizeEndpointRequest"/>.
/// </summary>
public record SanitizeRequest(string Content);

/// <summary>
/// Payload for POST /api/config/sanitize.
/// </summary>
/// <param name="Text">Primary field — text to sanitize. Accepts empty string.</param>
/// <param name="Content">Legacy alias for <paramref name="Text"/>. Kept so older
/// clients that still send <c>content</c> keep working during the cut-over.</param>
/// <param name="Context">Optional caller-supplied context string (e.g. the
/// channel or LLM message role) used for observability only; never applied to
/// rule selection.</param>
/// <param name="Direction">Either <c>"input"</c> (default) or <c>"output"</c>.
/// Selects which sanitisation pipeline to run — finding 006.</param>
public record SanitizeEndpointRequest(string? Text, string? Content, string? Context, string? Direction = null);

/// <summary>
/// Single diagnostic event ingest payload. Restored fields (finding 008):
/// <c>InputTokens</c>, <c>OutputTokens</c>, <c>EventType</c>, <c>AgentType</c>,
/// <c>ProjectId</c>, <c>EngineId</c>, <c>TaskId</c>, <c>TaskType</c>,
/// <c>CorrelationId</c>, <c>ErrorCode</c>. All optional so existing callers
/// continue to work.
/// </summary>
public record IngestDiagnosticRequest(
    string ProviderKey,
    double DurationMs,
    int TokensUsed,
    decimal Cost,
    string? Model,
    bool Success,
    string? Error,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? EventType = null,
    string? AgentType = null,
    string? ProjectId = null,
    string? EngineId = null,
    string? TaskId = null,
    string? TaskType = null,
    Guid? CorrelationId = null,
    string? ErrorCode = null);
public record CreateProviderRequest(string Type, object Config);
public record ExecuteProviderRequest(object[] Messages, object? Options);
