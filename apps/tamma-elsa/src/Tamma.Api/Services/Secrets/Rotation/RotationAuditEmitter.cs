using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC5 — default implementation of
/// <see cref="IRotationAuditEmitter"/> that forwards rotation events
/// into <see cref="IPlatformEventPublisher"/> (platform-scoped) — the
/// platform-event log is the canonical feed the admin dashboard
/// (Story 28-6) already subscribes to for other lifecycle events.
///
/// <para>Tenant-scoped events (when the rotation target carries a
/// non-null <c>TenantId</c>) are also published to the platform log
/// tagged with the tenant id so operator searches by tenant work. A
/// separate write into the tenant's <c>domain_events</c> can be added
/// later via a secondary ports map — the baseline here keeps the
/// feed unified.</para>
/// </summary>
public sealed class RotationAuditEmitter : IRotationAuditEmitter
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RotationAuditEmitter> _logger;

    public RotationAuditEmitter(
        IServiceProvider services,
        ILogger<RotationAuditEmitter> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task EmitAsync(RotationAuditEvent evt, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var publisher = scope.ServiceProvider.GetService<IPlatformEventPublisher>();
            if (publisher is null) return; // no sink wired — silent drop in dev/tests

            var (tags, data) = BuildTagsAndData(evt);
            var platformEvent = new PlatformEvent
            {
                Id = Guid.NewGuid(),
                Type = evt.EventType,
                TenantId = evt.TenantId,
                UserId = null,
                Tags = tags,
                Data = data,
                Metadata = "{\"source\":\"secret-rotation\"}",
                CreatedAt = evt.OccurredAt.UtcDateTime,
            };
            await publisher.AppendAndPublishAsync(platformEvent, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit rotation audit event {EventType} for secret {SecretId}",
                evt.EventType, evt.SecretId);
        }
    }

    private static (string Tags, string Data) BuildTagsAndData(RotationAuditEvent evt)
    {
        var tags = new Dictionary<string, object?>
        {
            ["secretId"] = evt.SecretId,
            ["rotationCorrelationId"] = evt.RotationCorrelationId,
            ["tenantId"] = evt.TenantId,
            ["versionNumber"] = evt.VersionNumber,
        };
        var dataDict = new Dictionary<string, object?>(evt.Data);
        if (!string.IsNullOrEmpty(evt.Detail))
            dataDict["detail"] = evt.Detail;
        return (JsonSerializer.Serialize(tags), JsonSerializer.Serialize(dataDict));
    }
}
