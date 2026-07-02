using System.Text.Json;
using Tamma.Api.Dtos.Agents;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 32-6 (AC5) — the tenant-scoped, member-readable per-agent action-trail
/// read API. Both endpoints are thin projections over
/// <see cref="IEventRepository.QueryAgentTrailAsync"/> so tenant isolation (AC4)
/// is INHERITED from the repository's schema-per-tenant scoping, not
/// re-implemented here. Mounted under <c>/api/v1/orgs/{tenantId}</c> behind
/// <c>RequireTenantMembershipFilter</c> (the same path-tenant gate the alert
/// endpoints ride) — read is member-level, there is no mutation surface.
/// </summary>
public static class AgentTrailEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;

    /// <summary>
    /// <c>GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs</c> — paginated list
    /// of runs (one row per terminal <c>AGENT.TASK.*</c>) for the agent within
    /// the tenant. Filterable by <paramref name="from"/>/<paramref name="to"/>
    /// date, <paramref name="role"/>, <paramref name="provider"/>, and
    /// <paramref name="outcome"/> (<c>success|failed|partial</c>).
    /// </summary>
    public static async Task<IResult> ListRuns(
        HttpContext http,
        IEventRepository events,
        Guid tenantId,
        Guid agentId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? role = null,
        string? provider = null,
        string? outcome = null,
        long? cursor = null,
        int? limit = null)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (agentId == Guid.Empty)
            return Results.BadRequest(new { error = "agentId must be a non-empty Guid" });

        var take = Math.Min(limit is > 0 ? limit.Value : DefaultPageSize, MaxPageSize);

        var (rows, total) = await events.QueryAgentTrailAsync(
            tenantId, agentId,
            typePrefix: AgentTrailEventTypes.TaskPrefix,
            from, to, role, provider, outcome,
            cursor, take).ConfigureAwait(false);

        var items = rows.Select(ToRunDto).ToList();
        var nextCursor = rows.Count == take && rows.Count > 0
            ? rows[^1].SequenceNumber
            : (long?)null;

        return Results.Ok(new AgentTrailPage<AgentRunDto>(
            items, total, nextCursor, nextCursor is not null));
    }

    /// <summary>
    /// <c>GET /api/v1/orgs/{tenantId}/agents/{agentId}/trail</c> — paginated flat
    /// stream of all trail events for the agent within the tenant (runs, tool
    /// calls, iterations, panels, bugs). Same filters as
    /// <see cref="ListRuns"/> plus an optional <paramref name="type"/> prefix.
    /// </summary>
    public static async Task<IResult> ListTrail(
        HttpContext http,
        IEventRepository events,
        Guid tenantId,
        Guid agentId,
        string? type = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? role = null,
        string? provider = null,
        string? outcome = null,
        long? cursor = null,
        int? limit = null)
    {
        if (tenantId == Guid.Empty)
            return Results.BadRequest(new { error = "tenantId must be a non-empty Guid" });
        if (agentId == Guid.Empty)
            return Results.BadRequest(new { error = "agentId must be a non-empty Guid" });

        var take = Math.Min(limit is > 0 ? limit.Value : DefaultPageSize, MaxPageSize);

        var (rows, total) = await events.QueryAgentTrailAsync(
            tenantId, agentId,
            typePrefix: string.IsNullOrWhiteSpace(type) ? null : type,
            from, to, role, provider, outcome,
            cursor, take).ConfigureAwait(false);

        var items = rows.Select(ToTrailDto).ToList();
        var nextCursor = rows.Count == take && rows.Count > 0
            ? rows[^1].SequenceNumber
            : (long?)null;

        return Results.Ok(new AgentTrailPage<AgentTrailEventDto>(
            items, total, nextCursor, nextCursor is not null));
    }

    // -----------------------------------------------------------------------
    // projections
    // -----------------------------------------------------------------------

    private static AgentRunDto ToRunDto(DomainEvent e)
    {
        var tags = ParseTags(e.Tags);
        var data = ParseData(e.Data);

        return new AgentRunDto(
            Id: e.Id,
            SequenceNumber: e.SequenceNumber,
            Type: e.Type,
            Outcome: OutcomeFromType(e.Type),
            AgentId: Tag(tags, "agentId"),
            AgentVersion: Tag(tags, "agentVersion"),
            Role: Tag(tags, "role"),
            Provider: Tag(tags, "provider"),
            Model: Tag(tags, "model"),
            CredentialSource: Tag(tags, "credentialSource"),
            CorrelationId: Tag(tags, "correlationId"),
            IssueId: Tag(tags, "issueId"),
            DurationMs: DataLong(data, "durationMs"),
            Iterations: DataInt(data, "iterations"),
            InputTokens: DataInt(data, "inputTokens"),
            OutputTokens: DataInt(data, "outputTokens"),
            CostUsd: DataDecimal(data, "costUsd"),
            FailureCode: DataString(data, "failureCode"),
            CreatedAt: e.CreatedAt);
    }

    private static AgentTrailEventDto ToTrailDto(DomainEvent e)
    {
        var tags = ParseTags(e.Tags);

        return new AgentTrailEventDto(
            Id: e.Id,
            SequenceNumber: e.SequenceNumber,
            Type: e.Type,
            AgentId: Tag(tags, "agentId"),
            AgentVersion: Tag(tags, "agentVersion"),
            Role: Tag(tags, "role"),
            Provider: Tag(tags, "provider"),
            Model: Tag(tags, "model"),
            CredentialSource: Tag(tags, "credentialSource"),
            CorrelationId: Tag(tags, "correlationId"),
            IssueId: Tag(tags, "issueId"),
            Iteration: Tag(tags, "iteration"),
            BugType: Tag(tags, "bugType"),
            Data: ParseData(e.Data),
            CreatedAt: e.CreatedAt);
    }

    private static string OutcomeFromType(string type) => type switch
    {
        AgentTrailEventTypes.TaskSuccess => "success",
        AgentTrailEventTypes.TaskFailed => "failed",
        AgentTrailEventTypes.TaskPartial => "partial",
        _ => "unknown",
    };

    private static Dictionary<string, JsonElement> ParseTags(string tagsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tagsJson)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    private static JsonElement ParseData(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private static string? Tag(IReadOnlyDictionary<string, JsonElement> tags, string key)
        => tags.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : (tags.TryGetValue(key, out var raw) && raw.ValueKind != JsonValueKind.Null
                ? raw.ToString()
                : null);

    private static long? DataLong(JsonElement data, string key)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var l)
            ? l : null;

    private static int? DataInt(JsonElement data, string key)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i)
            ? i : null;

    private static decimal? DataDecimal(JsonElement data, string key)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetDecimal(out var d)
            ? d : null;

    private static string? DataString(JsonElement data, string key)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty(key, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
