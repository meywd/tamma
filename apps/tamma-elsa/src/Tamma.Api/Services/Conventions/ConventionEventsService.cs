using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Best-effort emitter of convention-related DCB events.
/// Mirrors <see cref="Tamma.Api.Services.PromptStore.PromptEventsService"/> in
/// semantics and structure: events are emitted for every successful convention
/// mutation; emission never throws to callers (best-effort, swallows event-store
/// failures so the originating mutation continues).
///
/// <para>
/// Event types emitted (pattern: <c>CONVENTION.&lt;ACTION&gt;.SUCCESS</c>):
/// <list type="bullet">
///   <item><c>CONVENTION.CREATED.SUCCESS</c> — tenant override inserted.</item>
///   <item><c>CONVENTION.UPDATED.SUCCESS</c> — tenant override updated.</item>
///   <item><c>CONVENTION.DELETED.SUCCESS</c> — tenant override deleted (only when
///     a row was actually removed; no-op deletes emit nothing).</item>
///   <item><c>CONVENTION.SYSTEM_DEFAULT_CREATED.SUCCESS</c> — system default inserted.</item>
///   <item><c>CONVENTION.SYSTEM_DEFAULT_UPDATED.SUCCESS</c> — system default updated.</item>
///   <item><c>CONVENTION.SYSTEM_DEFAULT_DELETED.SUCCESS</c> — system default deleted.</item>
///   <item><c>CONVENTION.SYSTEM_DEFAULT_RESET.SUCCESS</c> — system default reset to
///     code baseline.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConventionEventsService
{
    public const string CreatedType = "CONVENTION.CREATED.SUCCESS";
    public const string UpdatedType = "CONVENTION.UPDATED.SUCCESS";
    public const string DeletedType = "CONVENTION.DELETED.SUCCESS";
    public const string SystemDefaultCreatedType = "CONVENTION.SYSTEM_DEFAULT_CREATED.SUCCESS";
    public const string SystemDefaultUpdatedType = "CONVENTION.SYSTEM_DEFAULT_UPDATED.SUCCESS";
    public const string SystemDefaultDeletedType = "CONVENTION.SYSTEM_DEFAULT_DELETED.SUCCESS";
    public const string SystemDefaultResetType = "CONVENTION.SYSTEM_DEFAULT_RESET.SUCCESS";

    private readonly IEventRepository _events;
    private readonly ILogger<ConventionEventsService>? _logger;

    public ConventionEventsService(IEventRepository events, ILogger<ConventionEventsService>? logger = null)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Emit <c>CONVENTION.CREATED.SUCCESS</c> or <c>CONVENTION.UPDATED.SUCCESS</c>
    /// for a tenant-override upsert, based on <paramref name="wasCreated"/>.
    /// </summary>
    public Task EmitTenantOverrideUpsertedAsync(
        Guid tenantId,
        AgentRole role,
        AgentAction action,
        Guid actorUserId,
        bool wasCreated,
        int newVersion,
        CancellationToken ct)
        => EmitAsync(
            wasCreated ? CreatedType : UpdatedType,
            tenantId,
            actorUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>
            {
                ["wasCreated"] = wasCreated,
                ["version"] = newVersion,
            },
            ct);

    /// <summary>
    /// Emit <c>CONVENTION.DELETED.SUCCESS</c> for a tenant-override deletion.
    /// Emits NOTHING when <paramref name="wasDeleted"/> is false (no-op delete
    /// must not produce a misleading audit event).
    /// </summary>
    public Task EmitTenantOverrideDeletedAsync(
        Guid tenantId,
        AgentRole role,
        AgentAction action,
        Guid actorUserId,
        bool wasDeleted,
        CancellationToken ct)
    {
        if (!wasDeleted) return Task.CompletedTask;
        return EmitAsync(
            DeletedType,
            tenantId,
            actorUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>(),
            ct);
    }

    /// <summary>
    /// Emit <c>CONVENTION.SYSTEM_DEFAULT_CREATED.SUCCESS</c> or
    /// <c>CONVENTION.SYSTEM_DEFAULT_UPDATED.SUCCESS</c> for a system-default
    /// upsert. Tags omit <c>tenantId</c> — this is a platform-wide event.
    /// </summary>
    public Task EmitSystemDefaultUpsertedAsync(
        AgentRole role,
        AgentAction action,
        Guid adminUserId,
        bool wasCreated,
        int newVersion,
        CancellationToken ct)
        => EmitAsync(
            wasCreated ? SystemDefaultCreatedType : SystemDefaultUpdatedType,
            tenantId: null,
            adminUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>
            {
                ["wasCreated"] = wasCreated,
                ["version"] = newVersion,
            },
            ct);

    /// <summary>
    /// Emit <c>CONVENTION.SYSTEM_DEFAULT_DELETED.SUCCESS</c>.
    /// Emits NOTHING when <paramref name="wasDeleted"/> is false.
    /// </summary>
    public Task EmitSystemDefaultDeletedAsync(
        AgentRole role,
        AgentAction action,
        Guid adminUserId,
        bool wasDeleted,
        CancellationToken ct)
    {
        if (!wasDeleted) return Task.CompletedTask;
        return EmitAsync(
            SystemDefaultDeletedType,
            tenantId: null,
            adminUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>(),
            ct);
    }

    /// <summary>
    /// Emit <c>CONVENTION.SYSTEM_DEFAULT_RESET.SUCCESS</c>. Always emits
    /// (reset is always an explicit admin action with an observable outcome).
    /// </summary>
    public Task EmitSystemDefaultResetAsync(
        AgentRole role,
        AgentAction action,
        Guid adminUserId,
        CancellationToken ct)
        => EmitAsync(
            SystemDefaultResetType,
            tenantId: null,
            adminUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>(),
            ct);

    // -----------------------------------------------------------------------
    // Internal — builds the domain event and swallows failures
    // -----------------------------------------------------------------------

    private async Task EmitAsync(
        string type,
        Guid? tenantId,
        Guid actorUserId,
        string role,
        string action,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct)
    {
        try
        {
            var tags = new Dictionary<string, string?>
            {
                ["role"] = role,
                ["action"] = action,
                ["userId"] = actorUserId.ToString(),
            };
            if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString();

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

            await _events.AppendAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: never block a convention mutation on event-store failure.
            _logger?.LogWarning(ex,
                "Failed to emit convention event {Type} for {Role}/{Action}",
                type, role, action);
        }
    }
}
