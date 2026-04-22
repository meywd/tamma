using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.PromptStore;

/// <summary>
/// Best-effort emitter of prompt-related DCB events.
/// Ported from <c>packages/api/src/services/prompt-store-events.ts</c>.
/// <para>
/// Event emission never throws to callers: if the event store is unavailable or
/// rejects an append, the error is logged and the originating prompt mutation
/// continues. This matches the TypeScript implementation's best-effort semantics.
/// </para>
/// </summary>
public sealed class PromptEventsService
{
    public const string CreatedType = "PROMPT.CREATED.SUCCESS";
    public const string UpdatedType = "PROMPT.UPDATED.SUCCESS";
    public const string DeletedType = "PROMPT.DELETED.SUCCESS";
    public const string ResetType = "PROMPT.RESET.SUCCESS";
    public const string RenderedType = "PROMPT.RENDERED.SUCCESS";

    private readonly IEventRepository _events;
    private readonly ILogger<PromptEventsService>? _logger;

    public PromptEventsService(IEventRepository events, ILogger<PromptEventsService>? logger = null)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>Emit a <c>PROMPT.UPDATED.SUCCESS</c> event.</summary>
    public Task EmitUpdatedAsync(
        Guid? tenantId,
        Guid? userId,
        string role,
        string action,
        IReadOnlyDictionary<string, object?> data)
        => EmitAsync(UpdatedType, tenantId, userId, role, action, data);

    /// <summary>Emit a <c>PROMPT.CREATED.SUCCESS</c> event.</summary>
    public Task EmitCreatedAsync(
        Guid? tenantId,
        Guid? userId,
        string role,
        string action,
        IReadOnlyDictionary<string, object?> data)
        => EmitAsync(CreatedType, tenantId, userId, role, action, data);

    /// <summary>Emit a <c>PROMPT.DELETED.SUCCESS</c> event.</summary>
    public Task EmitDeletedAsync(Guid? tenantId, Guid? userId, string role, string action)
        => EmitAsync(DeletedType, tenantId, userId, role, action, new Dictionary<string, object?>());

    /// <summary>Emit a <c>PROMPT.RESET.SUCCESS</c> event (override deleted, falls back to default).</summary>
    public Task EmitResetAsync(Guid? tenantId, Guid? userId, string role, string action)
        => EmitAsync(ResetType, tenantId, userId, role, action, new Dictionary<string, object?>());

    /// <summary>Emit a <c>PROMPT.RENDERED.SUCCESS</c> event with template metadata.</summary>
    public Task EmitRenderedAsync(
        Guid? tenantId,
        Guid? userId,
        string role,
        string action,
        int variableCount,
        int unresolvedCount)
        => EmitAsync(RenderedType, tenantId, userId, role, action, new Dictionary<string, object?>
        {
            ["variableCount"] = variableCount,
            ["unresolvedCount"] = unresolvedCount,
        });

    // -----------------------------------------------------------------------
    // Internal — builds the domain event and swallows failures
    // -----------------------------------------------------------------------

    private async Task EmitAsync(
        string type,
        Guid? tenantId,
        Guid? userId,
        string role,
        string action,
        IReadOnlyDictionary<string, object?> data)
    {
        try
        {
            var tags = new Dictionary<string, string?>
            {
                ["role"] = role,
                ["action"] = action,
            };
            if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString();
            if (userId is not null) tags["userId"] = userId.Value.ToString();

            var metadata = new Dictionary<string, object?>
            {
                ["workflowVersion"] = "1.0.0",
                ["eventSource"] = "system",
            };

            var evt = new DomainEvent
            {
                Type = type,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(tags),
                Metadata = JsonSerializer.Serialize(metadata),
                Data = JsonSerializer.Serialize(data),
            };

            await _events.AppendAsync(evt);
        }
        catch (Exception ex)
        {
            // Best-effort: never block the prompt mutation on event-store failure.
            _logger?.LogWarning(ex,
                "Failed to emit prompt event {Type} for {Role}/{Action}",
                type, role, action);
        }
    }
}
