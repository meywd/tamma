using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.AcceptanceRules;

/// <summary>
/// Best-effort emitter of acceptance-rules DCB events (Story 39-5 Design Decision
/// D12), mirroring <c>PromptEventsService</c>. Emission never throws to callers:
/// if the event store is unavailable the error is logged and the originating
/// mutation continues.
/// </summary>
public sealed class AcceptanceRulesEventsService
{
    public const string CreatedType = "ACCEPTANCE_RULES.CREATED.SUCCESS";
    public const string UpdatedType = "ACCEPTANCE_RULES.UPDATED.SUCCESS";
    public const string ResetType = "ACCEPTANCE_RULES.RESET.SUCCESS";

    private readonly IEventRepository _events;
    private readonly ILogger<AcceptanceRulesEventsService>? _logger;

    public AcceptanceRulesEventsService(IEventRepository events, ILogger<AcceptanceRulesEventsService>? logger = null)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>Emit <c>ACCEPTANCE_RULES.CREATED.SUCCESS</c>.</summary>
    public Task EmitCreatedAsync(Guid? tenantId, Guid? userId, string documentTypeKey, int autonomyLevel, int version)
        => EmitAsync(CreatedType, tenantId, userId, documentTypeKey, new Dictionary<string, object?>
        {
            ["documentTypeKey"] = documentTypeKey,
            ["autonomyLevel"] = autonomyLevel,
            ["version"] = version,
        });

    /// <summary>Emit <c>ACCEPTANCE_RULES.UPDATED.SUCCESS</c>.</summary>
    public Task EmitUpdatedAsync(Guid? tenantId, Guid? userId, string documentTypeKey, int autonomyLevel, int version)
        => EmitAsync(UpdatedType, tenantId, userId, documentTypeKey, new Dictionary<string, object?>
        {
            ["documentTypeKey"] = documentTypeKey,
            ["autonomyLevel"] = autonomyLevel,
            ["version"] = version,
        });

    /// <summary>Emit <c>ACCEPTANCE_RULES.RESET.SUCCESS</c> (override deleted → falls back to default).</summary>
    public Task EmitResetAsync(Guid? tenantId, Guid? userId, string documentTypeKey)
        => EmitAsync(ResetType, tenantId, userId, documentTypeKey, new Dictionary<string, object?>
        {
            ["documentTypeKey"] = documentTypeKey,
        });

    private async Task EmitAsync(
        string type,
        Guid? tenantId,
        Guid? userId,
        string documentTypeKey,
        IReadOnlyDictionary<string, object?> data)
    {
        try
        {
            var tags = new Dictionary<string, string?>
            {
                ["documentTypeKey"] = documentTypeKey,
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
            _logger?.LogWarning(ex,
                "Failed to emit acceptance-rules event {Type} for {DocumentTypeKey}",
                type, documentTypeKey);
        }
    }
}
