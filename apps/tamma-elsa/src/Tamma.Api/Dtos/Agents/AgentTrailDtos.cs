namespace Tamma.Api.Dtos.Agents;

/// <summary>
/// Story 32-6 — wire DTOs for the per-agent action-trail read API. Both the
/// <c>/runs</c> and <c>/trail</c> endpoints page on <c>SequenceNumber</c> and
/// return <see cref="NextCursor"/>/<see cref="HasMore"/>.
/// </summary>
/// <typeparam name="T">The item shape (<see cref="AgentRunDto"/> or
/// <see cref="AgentTrailEventDto"/>).</typeparam>
/// <param name="Total">Exact match count — <c>null</c> unless the caller passed
/// <c>includeTotal=true</c> (the count is an unbounded scan; it is opt-in).
/// <c>null</c> means "not computed", NOT "zero". Pagination uses
/// <paramref name="HasMore"/>/<paramref name="NextCursor"/>, never the total.</param>
public sealed record AgentTrailPage<T>(
    IReadOnlyList<T> Items,
    int? Total,
    long? NextCursor,
    bool HasMore);

/// <summary>
/// Story 32-6 — one run row for <c>GET /agents/{agentId}/runs</c> (a terminal
/// <c>AGENT.TASK.*</c> event projected). Identity fields come from the event's
/// <c>Tags</c>; metrics come from its <c>Data</c>.
/// </summary>
public sealed record AgentRunDto(
    Guid Id,
    long SequenceNumber,
    string Type,
    string Outcome,            // success | failed | partial
    string? AgentId,
    string? AgentVersion,
    string? Role,
    string? Provider,
    string? Model,
    string? CredentialSource,
    string? CorrelationId,
    string? IssueId,
    long? DurationMs,
    int? Iterations,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    string? FailureCode,
    DateTime CreatedAt);

/// <summary>
/// Story 32-6 — one trail event for <c>GET /agents/{agentId}/trail</c> (the flat
/// stream of every <c>AGENT.*</c>/<c>REVIEW.BUG.*</c> event for the agent).
/// Carries the flat identity tags plus the event's raw <c>Data</c> JSON (already
/// blob-referenced + sanitized by the emitter).
/// </summary>
public sealed record AgentTrailEventDto(
    Guid Id,
    long SequenceNumber,
    string Type,
    string? AgentId,
    string? AgentVersion,
    string? Role,
    string? Provider,
    string? Model,
    string? CredentialSource,
    string? CorrelationId,
    string? IssueId,
    string? Iteration,
    string? BugType,
    System.Text.Json.JsonElement Data,
    DateTime CreatedAt);
