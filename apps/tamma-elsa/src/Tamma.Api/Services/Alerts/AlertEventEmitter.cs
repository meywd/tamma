using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Core.Redaction;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Alerts;

/// <summary>
/// Wave C.4 — default <see cref="IAlertEventEmitter"/>. Builds the
/// <see cref="DomainEvent"/> / <see cref="PlatformEvent"/> rows the
/// <c>AlertRuleEvaluator</c> polls for and routes each event to the right
/// plane:
///
/// <list type="bullet">
///   <item><description>Tenant-scoped events (<c>BUDGET.EXHAUSTED</c>,
///     <c>AGENT.DISPATCH.FAILED</c>, <c>WORKFLOW.RETRY_EXCEEDED</c>,
///     tenant-bound <c>SECRET.ROTATION.FAILED</c>) → tenant
///     <c>domain_events</c> via <see cref="IEventRepository"/>.</description></item>
///   <item><description><c>PLATFORM.API.UNHEALTHY</c> +
///     platform-scoped rotations → <c>platform_events</c> via
///     <see cref="IPlatformEventPublisher"/>.</description></item>
/// </list>
///
/// <para>Every <c>lastError</c> / <c>finalError</c> field is scrubbed
/// through <see cref="CredentialRedactor.Clean"/> before persistence —
/// the event store must never become a credential-leak vector.</para>
/// </summary>
public sealed class AlertEventEmitter : IAlertEventEmitter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly IEventRepository _events;
    private readonly IPlatformEventPublisher _platform;
    private readonly ILogger<AlertEventEmitter> _logger;

    public AlertEventEmitter(
        IEventRepository events,
        IPlatformEventPublisher platform,
        ILogger<AlertEventEmitter> logger)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EmitBudgetExhaustedAsync(
        BudgetExhaustedEvent evt, CancellationToken ct)
    {
        try
        {
            var tags = new Dictionary<string, string>
            {
                ["tenantId"] = evt.TenantId.ToString(),
                ["correlationId"] = evt.CorrelationId,
                ["providerName"] = evt.ProviderName,
                ["source"] = evt.Source,
            };
            var data = new Dictionary<string, object?>
            {
                ["source"] = evt.Source,
                ["spent"] = evt.Spent,
                ["limit"] = evt.Limit,
                ["providerName"] = evt.ProviderName,
                ["workflowInstanceId"] = evt.WorkflowInstanceId,
            };
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "BUDGET.EXHAUSTED",
                TenantId = evt.TenantId,
                Tags = JsonSerializer.Serialize(tags, JsonOpts),
                Metadata = """{"eventSource":"system","workflowVersion":"1.0.0"}""",
                Data = JsonSerializer.Serialize(data, JsonOpts),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BUDGET.EXHAUSTED emission failed for tenant {TenantId}",
                evt.TenantId);
        }

        _ = ct; // persistence is itself synchronous-ish; cancellation isn't plumbed through IEventRepository.
    }

    public async Task EmitAgentDispatchFailedAsync(
        AgentDispatchFailedEvent evt, CancellationToken ct)
    {
        try
        {
            var redactedError = CredentialRedactor.Clean(evt.LastError);
            var tags = new Dictionary<string, string>
            {
                ["tenantId"] = evt.TenantId.ToString(),
                ["correlationId"] = evt.CorrelationId,
                ["agentHandle"] = evt.AgentHandle,
                ["reason"] = evt.Reason,
            };
            var data = new Dictionary<string, object?>
            {
                ["agentHandle"] = evt.AgentHandle,
                ["reason"] = evt.Reason,
                ["attemptNumber"] = evt.AttemptNumber,
                ["lastError"] = redactedError,
            };
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "AGENT.DISPATCH.FAILED",
                TenantId = evt.TenantId,
                Tags = JsonSerializer.Serialize(tags, JsonOpts),
                Metadata = """{"eventSource":"system","workflowVersion":"1.0.0"}""",
                Data = JsonSerializer.Serialize(data, JsonOpts),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AGENT.DISPATCH.FAILED emission failed for tenant {TenantId}",
                evt.TenantId);
        }
        _ = ct;
    }

    public async Task EmitWorkflowRetryExceededAsync(
        WorkflowRetryExceededEvent evt, CancellationToken ct)
    {
        try
        {
            var redactedError = CredentialRedactor.Clean(evt.FinalError);
            var tags = new Dictionary<string, string?>
            {
                ["tenantId"] = evt.TenantId.ToString(),
                ["correlationId"] = evt.CorrelationId,
                ["workflowDefinitionId"] = evt.WorkflowDefinitionId.ToString(),
            };
            var data = new Dictionary<string, object?>
            {
                ["workflowDefinitionId"] = evt.WorkflowDefinitionId.ToString(),
                ["workflowInstanceId"] = evt.WorkflowInstanceId.ToString(),
                ["attempts"] = evt.Attempts,
                ["maxAttempts"] = evt.MaxAttempts,
                ["finalError"] = redactedError,
                ["activityId"] = evt.ActivityId,
            };
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "WORKFLOW.RETRY_EXCEEDED",
                TenantId = evt.TenantId,
                Tags = JsonSerializer.Serialize(tags, JsonOpts),
                Metadata = """{"eventSource":"system","workflowVersion":"1.0.0"}""",
                Data = JsonSerializer.Serialize(data, JsonOpts),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WORKFLOW.RETRY_EXCEEDED emission failed for workflow {WorkflowInstanceId}",
                evt.WorkflowInstanceId);
        }
        _ = ct;
    }

    public async Task EmitPlatformApiUnhealthyAsync(
        PlatformApiUnhealthyEvent evt, CancellationToken ct)
    {
        try
        {
            var tags = new Dictionary<string, string>
            {
                ["platform"] = "tamma-api",
            };
            var data = new Dictionary<string, object?>
            {
                ["windowSeconds"] = evt.WindowSeconds,
                ["totalRequests"] = evt.TotalRequests,
                ["failureCount"] = evt.FailureCount,
                ["failureRate"] = evt.FailureRate,
                ["topFailureReasons"] = evt.TopFailureReasons
                    .Select(r => new { reason = r.Reason, count = r.Count })
                    .ToArray(),
            };
            await _platform.AppendAndPublishAsync(new PlatformEvent
            {
                Id = Guid.NewGuid(),
                Type = "PLATFORM.API.UNHEALTHY",
                TenantId = null,
                Tags = JsonSerializer.Serialize(tags, JsonOpts),
                Metadata = """{"eventSource":"system","workflowVersion":"1.0.0"}""",
                Data = JsonSerializer.Serialize(data, JsonOpts),
                CreatedAt = DateTime.UtcNow,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PLATFORM.API.UNHEALTHY emission failed");
        }
    }

    public async Task EmitSecretRotationFailedAsync(
        SecretRotationFailedEvent evt, CancellationToken ct)
    {
        try
        {
            var redactedError = CredentialRedactor.Clean(evt.LastError);
            var tags = new Dictionary<string, string?>
            {
                ["tenantId"] = evt.TenantId?.ToString(),
                ["correlationId"] = evt.CorrelationId,
                ["cabinetName"] = evt.CabinetName,
                ["handlerType"] = evt.HandlerType,
            };
            var data = new Dictionary<string, object?>
            {
                ["targetKind"] = evt.TargetKind,
                ["cabinetName"] = evt.CabinetName,
                ["handlerType"] = evt.HandlerType,
                ["failureStage"] = evt.FailureStage,
                ["compensationApplied"] = evt.CompensationApplied,
                ["lastError"] = redactedError,
            };

            if (evt.TenantId is Guid tenantId)
            {
                await _events.AppendAsync(new DomainEvent
                {
                    Id = Guid.NewGuid(),
                    Type = "SECRET.ROTATION.FAILED",
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
                    Type = "SECRET.ROTATION.FAILED",
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
                "SECRET.ROTATION.FAILED emission failed for cabinet {Cabinet}",
                evt.CabinetName);
        }
        _ = ct;
    }
}
