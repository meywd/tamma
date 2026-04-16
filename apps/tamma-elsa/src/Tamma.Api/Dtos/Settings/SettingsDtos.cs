namespace Tamma.Api.Dtos.Settings;

public record UpdateAgentsConfigRequest(object Config);
public record UpdateSecurityConfigRequest(object Config);
public record UpdateSanitizationRulesRequest(object Rules);
public record SanitizeRequest(string Content);
public record IngestDiagnosticRequest(string ProviderKey, double DurationMs, int TokensUsed, decimal Cost, string? Model, bool Success, string? Error);
public record CreateProviderRequest(string Type, object Config);
public record ExecuteProviderRequest(object[] Messages, object? Options);
