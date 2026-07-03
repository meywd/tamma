using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Alerts;
using Tamma.Core.Audit;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC9/AC10) — emits the <c>AUDIT.CHAIN.*</c> DCB events and raises
/// the critical tamper alert. Plane routing mirrors <see cref="AlertEventEmitter"/>:
/// tenant-scope events go to the tenant's <c>domain_events</c> via
/// <see cref="IEventRepository"/>; platform-scope events go to
/// <c>platform_events</c> via <see cref="IPlatformEventPublisher"/>. No record
/// payload contents are ever copied into event data — only hashes + coordinates.
/// </summary>
public sealed class AuditChainEventEmitter : IAuditChainEventEmitter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IEventRepository _events;
    private readonly IPlatformEventPublisher _platform;
    private readonly IAlertSink _alerts;
    private readonly ILogger<AuditChainEventEmitter> _logger;

    public AuditChainEventEmitter(
        IEventRepository events,
        IPlatformEventPublisher platform,
        IAlertSink alerts,
        ILogger<AuditChainEventEmitter> logger)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EmitVerifiedAsync(
        AuditChainScope scope, ChainVerificationResult result, CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["scope"] = scope.Discriminator,
            ["tenantId"] = scope.TenantId?.ToString(),
            ["chainSequence"] = result.LastSequence.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["scope"] = scope.Discriminator,
            ["recordsVerified"] = result.RecordsVerified,
            ["headSequence"] = result.LastSequence,
            ["lastCheckpointSequence"] = result.LastCheckpoint?.HeadSequence,
        };
        await EmitAsync(AuditChainEventTypes.Verified, scope, tags, data, ct).ConfigureAwait(false);
    }

    public async Task EmitTamperAsync(
        AuditChainScope scope, ChainVerificationResult result, CancellationToken ct)
    {
        var link = result.FirstBrokenLink;
        var tags = new Dictionary<string, string?>
        {
            ["scope"] = scope.Discriminator,
            ["tenantId"] = scope.TenantId?.ToString(),
            ["chainSequence"] = link?.ChainSequence.ToString(),
            ["reason"] = link?.Reason.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["scope"] = scope.Discriminator,
            ["reason"] = link?.Reason.ToString(),
            ["chainSequence"] = link?.ChainSequence,
            ["recordId"] = link?.RecordId?.ToString(),
            ["recordsVerified"] = result.RecordsVerified,
        };
        await EmitAsync(AuditChainEventTypes.TamperDetected, scope, tags, data, ct)
            .ConfigureAwait(false);

        // AC10 — a tamper always raises a CRITICAL alert. Tenant-scope tampering
        // sets TenantId (tenant feed); platform-scope leaves it null (admin feed).
        try
        {
            await _alerts.RaiseAsync(new AlertPayload(
                Severity: AlertSeverity.Critical,
                Title: $"Audit chain tamper detected ({scope.Discriminator})",
                Description:
                    $"Chain verification found a broken link — reason={link?.Reason}, "
                    + $"chainSequence={link?.ChainSequence}. The audit trail for scope "
                    + $"'{scope.Discriminator}'"
                    + (scope.TenantId is Guid t ? $" (tenant {t})" : string.Empty)
                    + " may have been modified, deleted, reordered, or its checkpoint forged.",
                CorrelationId: null,
                TenantId: scope.TenantId,
                RuleId: null,
                Metadata: new Dictionary<string, object?>
                {
                    ["scope"] = scope.Discriminator,
                    ["reason"] = link?.Reason.ToString(),
                    ["chainSequence"] = link?.ChainSequence,
                }), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to raise critical alert for AUDIT.CHAIN.TAMPER_DETECTED (scope {Scope}).",
                scope.Discriminator);
        }
    }

    public async Task EmitCheckpointedAsync(
        AuditChainScope scope, long headSequence, int keyVersion, CancellationToken ct)
    {
        var tags = new Dictionary<string, string?>
        {
            ["scope"] = scope.Discriminator,
            ["tenantId"] = scope.TenantId?.ToString(),
            ["chainSequence"] = headSequence.ToString(),
        };
        var data = new Dictionary<string, object?>
        {
            ["scope"] = scope.Discriminator,
            ["headSequence"] = headSequence,
            ["keyVersion"] = keyVersion,
        };
        await EmitAsync(AuditChainEventTypes.Checkpointed, scope, tags, data, ct)
            .ConfigureAwait(false);
    }

    private async Task EmitAsync(
        string type, AuditChainScope scope,
        Dictionary<string, string?> tags, Dictionary<string, object?> data,
        CancellationToken ct)
    {
        try
        {
            if (scope.Kind == AuditChainScopeKind.Tenant && scope.TenantId is Guid tenantId)
            {
                await _events.AppendAsync(new DomainEvent
                {
                    Id = Guid.NewGuid(),
                    Type = type,
                    TenantId = tenantId,
                    Tags = JsonSerializer.Serialize(tags, JsonOpts),
                    Metadata = """{"eventSource":"system","workflowVersion":"1.0.0"}""",
                    Data = JsonSerializer.Serialize(data, JsonOpts),
                }).ConfigureAwait(false);
            }
            else
            {
                await _platform.AppendAndPublishAsync(new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = type,
                    TenantId = null,
                    Tags = JsonSerializer.Serialize(tags, JsonOpts),
                    Metadata = """{"eventSource":"system","workflowVersion":"1.0.0"}""",
                    Data = JsonSerializer.Serialize(data, JsonOpts),
                    CreatedAt = DateTime.UtcNow,
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AUDIT.CHAIN event {Type} emission failed (scope {Scope}).",
                type, scope.Discriminator);
        }
    }
}
