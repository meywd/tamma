using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms;

/// <summary>
/// Story 31-2 AC7 — typed event types emitted on installation
/// lifecycle transitions. Drained into the platform event log
/// (<see cref="IPlatformEventRepository"/>) as
/// <c>PLATFORM.INSTALLATION.*</c> with structured tags for filtering
/// + downstream subscribers (notifications, analytics, the cache
/// invalidator).
/// </summary>
public static class PlatformInstallationEventTypes
{
    public const string Connected = "PLATFORM.INSTALLATION.CONNECTED.SUCCESS";
    public const string Disconnected = "PLATFORM.INSTALLATION.DISCONNECTED.SUCCESS";
    public const string CredentialRotated =
        "PLATFORM.INSTALLATION.CREDENTIAL_ROTATED.SUCCESS";

    /// <summary>
    /// Cache-invalidation marker — emitted by 28-9's switch-org flow
    /// when the JWT's tenant claim changes; the resolver subscribes
    /// to invalidate the previous tenant's cached drivers.
    /// </summary>
    public const string SwitchOrg = "TENANT.SWITCH_ORG.SUCCESS";

    /// <summary>
    /// Diagnostic marker — emitted by the resolver itself when the
    /// cache is invalidated for any reason (so audit trail captures
    /// the cause).
    /// </summary>
    public const string ResolverCacheInvalidated =
        "PLATFORM.RESOLVER_CACHE.INVALIDATED";
}

/// <summary>
/// Emitter that callers (Story 31-3 GitHub refactor, Story 31-9
/// onboarding picker, Story 29-7 rotation) plumb in via DI to push
/// installation lifecycle events into the platform event log without
/// hand-rolling the JSON tag shape every time.
/// </summary>
public interface IPlatformInstallationEventEmitter
{
    /// <summary>
    /// Emit a <c>PLATFORM.INSTALLATION.CONNECTED.SUCCESS</c> event
    /// after a successful onboarding / install.
    /// </summary>
    Task EmitConnectedAsync(
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        string? installationExternalId,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Emit a <c>PLATFORM.INSTALLATION.DISCONNECTED.SUCCESS</c> event
    /// after soft-deleting an installation row.
    /// </summary>
    Task EmitDisconnectedAsync(
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        string? installationExternalId,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Emit a <c>PLATFORM.INSTALLATION.CREDENTIAL_ROTATED.SUCCESS</c>
    /// event after Story 29-7 rotation re-mints the installation
    /// credential. The resolver subscribes to invalidate the cached
    /// driver bound to the rotated credential.
    /// </summary>
    Task EmitCredentialRotatedAsync(
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        Guid? actorUserId,
        CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPlatformInstallationEventEmitter"/> backed by
/// the control-plane platform-event log
/// (<see cref="IPlatformEventRepository"/>). Tag shape mirrors the
/// existing GitHub installation events so the dashboard can filter
/// by tenant + kind + installation id without a schema bump.
/// </summary>
public sealed class PlatformInstallationEventEmitter
    : IPlatformInstallationEventEmitter
{
    private readonly IPlatformEventRepository _events;
    private readonly ILogger<PlatformInstallationEventEmitter> _logger;
    private readonly Func<PlatformEvent, CancellationToken, Task>? _postAppend;

    /// <param name="events">Durable platform-event log.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="postAppend">
    /// Epic 31 P2 — optional hook invoked AFTER a successful append.
    /// The composition root wires it to the in-process
    /// platform-event bus so subscribers (the driver-cache
    /// invalidator) see rotation / disconnect / switch-org events
    /// without polling the table. Failures are swallowed + logged —
    /// fan-out must never block the lifecycle transition.
    /// </param>
    public PlatformInstallationEventEmitter(
        IPlatformEventRepository events,
        ILogger<PlatformInstallationEventEmitter>? logger = null,
        Func<PlatformEvent, CancellationToken, Task>? postAppend = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        _logger = logger ?? NullLogger<PlatformInstallationEventEmitter>.Instance;
        _postAppend = postAppend;
    }

    /// <inheritdoc />
    public Task EmitConnectedAsync(
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        string? installationExternalId,
        Guid? actorUserId,
        CancellationToken ct = default) =>
        EmitAsync(
            PlatformInstallationEventTypes.Connected,
            tenantId, kind, installationRowId, installationExternalId,
            actorUserId, ct);

    /// <inheritdoc />
    public Task EmitDisconnectedAsync(
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        string? installationExternalId,
        Guid? actorUserId,
        CancellationToken ct = default) =>
        EmitAsync(
            PlatformInstallationEventTypes.Disconnected,
            tenantId, kind, installationRowId, installationExternalId,
            actorUserId, ct);

    /// <inheritdoc />
    public Task EmitCredentialRotatedAsync(
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        Guid? actorUserId,
        CancellationToken ct = default) =>
        EmitAsync(
            PlatformInstallationEventTypes.CredentialRotated,
            tenantId, kind, installationRowId,
            installationExternalId: null,
            actorUserId, ct);

    private async Task EmitAsync(
        string type,
        Guid tenantId,
        PlatformKind kind,
        Guid installationRowId,
        string? installationExternalId,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString(),
            ["platformKind"] = PlatformResolver.ToWireKind(kind),
            ["installationId"] = installationRowId.ToString(),
        };
        if (installationExternalId is not null)
        {
            tags["installationExternalId"] = installationExternalId;
        }

        var evt = new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            UserId = actorUserId,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = "{\"workflowVersion\":\"1.0.0\",\"eventSource\":\"system\"}",
            Data = "{}",
        };

        try
        {
            await _events.AppendAsync(evt, ct).ConfigureAwait(false);

            if (_postAppend is not null)
            {
                // In-process fan-out (cache invalidation). Best-effort:
                // a subscriber failure must not block the transition.
                try
                {
                    await _postAppend(evt, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Post-append fan-out for {EventType} failed (tenant {TenantId}); the "
                        + "durable event is already appended — cache TTL self-heals",
                        type, tenantId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to emit {EventType} for tenant {TenantId} kind {Kind} " +
                "installation {InstallationId} — caller swallows so the lifecycle " +
                "transition is not blocked by the audit log",
                type, tenantId, kind, installationRowId);
        }
    }
}
