using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Agents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Best-effort emitter of convention-related DCB events (Story 27-14).
/// Mirrors <see cref="Tamma.Api.Services.PromptStore.PromptEventsService"/> in
/// semantics and structure: events are emitted for every successful convention
/// mutation; emission never throws to callers (best-effort, swallows event-store
/// failures so the originating mutation continues).
///
/// <para>
/// <b>Unified event types (Story 27-14 Delta B).</b> FOUR types only:
/// <list type="bullet">
///   <item><c>CONVENTION.CREATED.SUCCESS</c> — row inserted (tenant or system).</item>
///   <item><c>CONVENTION.UPDATED.SUCCESS</c> — row updated (tenant or system).</item>
///   <item><c>CONVENTION.DELETED.SUCCESS</c> — row deleted (only when a row was
///     actually removed; no-op deletes emit nothing).</item>
///   <item><c>CONVENTION.RESET.SUCCESS</c> — system default reset to code
///     baseline.</item>
/// </list>
/// A <c>source</c> tag (<c>"tenant"</c> or <c>"system"</c>) distinguishes the
/// tier; tenant events also carry a <c>tenantId</c> tag.
/// </para>
///
/// <para>
/// <b>Emission site (Story 27-14 Delta A).</b> Methods on this service are called
/// from <see cref="ConventionStore"/> — NOT from the endpoint handlers. Any future
/// caller of <see cref="IConventionStore"/> (CLI, internal scripts, Elsa activities)
/// gets audit events automatically.
/// </para>
///
/// <para>
/// <b>changedFields diff (Story 27-14 Delta C).</b> UPDATED events carry
/// <c>data.previousVersion</c>, <c>data.newVersion</c>, and <c>data.changedFields</c>
/// — an array of field names (<c>"body"</c> and/or <c>"enabled"</c>) that changed
/// in the update.
/// </para>
/// </summary>
public sealed class ConventionEventsService
{
    public const string CreatedType = "CONVENTION.CREATED.SUCCESS";
    public const string UpdatedType = "CONVENTION.UPDATED.SUCCESS";
    public const string DeletedType = "CONVENTION.DELETED.SUCCESS";
    public const string ResetType   = "CONVENTION.RESET.SUCCESS";

    private readonly IEventRepository _events;
    private readonly ILogger<ConventionEventsService>? _logger;

    public ConventionEventsService(IEventRepository events, ILogger<ConventionEventsService>? logger = null)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Emit <c>CONVENTION.CREATED.SUCCESS</c> or <c>CONVENTION.UPDATED.SUCCESS</c>
    /// for a tenant-override or system-default upsert.
    /// <para>
    /// On update, <paramref name="previous"/> must be non-null so
    /// <c>changedFields</c> can be computed. On create (when
    /// <paramref name="wasCreated"/> is true) <paramref name="previous"/> is
    /// ignored.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Non-null for tenant overrides; null for system defaults.</param>
    /// <param name="role">Agent role.</param>
    /// <param name="action">Agent action.</param>
    /// <param name="actorUserId">User or admin performing the upsert.</param>
    /// <param name="wasCreated">True → emits CREATED; false → emits UPDATED.</param>
    /// <param name="previous">The row state before mutation (must be non-null when
    /// <paramref name="wasCreated"/> is false).</param>
    /// <param name="current">The persisted row after mutation.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task EmitUpsertedAsync(
        Guid? tenantId,
        AgentRole role,
        AgentAction action,
        Guid actorUserId,
        bool wasCreated,
        Convention? previous,
        Convention current,
        CancellationToken ct)
    {
        if (wasCreated)
        {
            return EmitAsync(
                CreatedType,
                tenantId,
                actorUserId,
                role.ToWire(),
                action.ToWire(),
                new Dictionary<string, object?>
                {
                    ["version"] = current.Version,
                    ["enabled"] = current.Enabled,
                },
                ct);
        }

        // Update: compute changedFields diff.
        var changedFields = new List<string>();
        if (previous is not null)
        {
            if (previous.Body != current.Body)
                changedFields.Add("body");
            if (previous.Enabled != current.Enabled)
                changedFields.Add("enabled");
        }

        return EmitAsync(
            UpdatedType,
            tenantId,
            actorUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>
            {
                ["previousVersion"] = previous?.Version,
                ["newVersion"]      = current.Version,
                ["changedFields"]   = changedFields,
            },
            ct);
    }

    /// <summary>
    /// Emit <c>CONVENTION.DELETED.SUCCESS</c> for a deletion.
    /// Emits NOTHING when <paramref name="wasDeleted"/> is false (no-op delete
    /// must not produce a misleading audit event).
    /// </summary>
    /// <param name="tenantId">Non-null for tenant overrides; null for system defaults.</param>
    /// <param name="role">Agent role.</param>
    /// <param name="action">Agent action.</param>
    /// <param name="actorUserId">User or admin performing the delete.</param>
    /// <param name="wasDeleted">False → emits nothing.</param>
    /// <param name="deletedVersion">Version of the deleted row (included in event data).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task EmitDeletedAsync(
        Guid? tenantId,
        AgentRole role,
        AgentAction action,
        Guid actorUserId,
        bool wasDeleted,
        int? deletedVersion,
        CancellationToken ct)
    {
        if (!wasDeleted) return Task.CompletedTask;
        return EmitAsync(
            DeletedType,
            tenantId,
            actorUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>
            {
                ["deletedVersion"] = deletedVersion,
            },
            ct);
    }

    /// <summary>
    /// Emit <c>CONVENTION.RESET.SUCCESS</c> for a system-default reset.
    /// Always emits (reset is always an explicit admin action with an
    /// observable outcome). The <c>source</c> tag is always <c>"system"</c>.
    /// </summary>
    /// <param name="role">Agent role.</param>
    /// <param name="action">Agent action.</param>
    /// <param name="adminUserId">Admin performing the reset.</param>
    /// <param name="previousVersion">Version of the row before the reset upsert.</param>
    /// <param name="newVersion">Version of the row after the reset upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task EmitResetAsync(
        AgentRole role,
        AgentAction action,
        Guid adminUserId,
        int previousVersion,
        int newVersion,
        CancellationToken ct)
        => EmitAsync(
            ResetType,
            tenantId: null,
            adminUserId,
            role.ToWire(),
            action.ToWire(),
            new Dictionary<string, object?>
            {
                ["previousVersion"] = previousVersion,
                ["newVersion"]      = newVersion,
                // IMP-3 (post-review rename): system defaults are now DB-managed
                // at runtime via admin CRUD (2026-05-25 decision). The previous
                // "hardcoded" label is no longer truthful — the reset source is
                // ConventionSeedSpecs (the code-baseline restored by ResetSystemDefaultAsync).
                // "admin-edited" → "code-baseline" reads as exactly what happened.
                ["resetFrom"]       = "admin-edited",
                ["resetTo"]         = "code-baseline",
            },
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
            var source = tenantId is null ? "system" : "tenant";
            var tags = new Dictionary<string, string?>
            {
                ["role"]   = role,
                ["action"] = action,
                ["source"] = source,
                ["userId"] = actorUserId.ToString(),
            };
            if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString();

            var metadata = new Dictionary<string, object?>
            {
                ["workflowVersion"] = "1.0.0",
                ["eventSource"]     = "system",
            };

            var evt = new DomainEvent
            {
                Type     = type,
                TenantId = tenantId,
                Tags     = JsonSerializer.Serialize(tags),
                Metadata = JsonSerializer.Serialize(metadata),
                Data     = JsonSerializer.Serialize(data),
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
