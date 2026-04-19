using System.Text.Json;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.SaaS;

/// <summary>
/// Concrete <see cref="IWorkflowLifecycleService"/>.
///
/// Ports the behaviour of the deleted TypeScript
/// <c>routes/saas/workflow-status.ts</c> and <c>workflow-result.ts</c>:
/// variables are merged shallow-style over the existing JSONB, terminal
/// results persist to the new <c>Result</c> column, and audit events are
/// emitted for every terminal state.
/// </summary>
public sealed class WorkflowLifecycleService : IWorkflowLifecycleService
{
    private readonly IWorkflowRepository _workflows;
    private readonly IEventRepository _events;
    private readonly ILogger<WorkflowLifecycleService> _logger;

    public WorkflowLifecycleService(
        IWorkflowRepository workflows,
        IEventRepository events,
        ILogger<WorkflowLifecycleService> logger)
    {
        _workflows = workflows;
        _events = events;
        _logger = logger;
    }

    public async Task<WorkflowLifecycleResult> UpdateStatusAsync(
        Guid instanceId,
        string status,
        JsonElement? variables,
        string? currentActivity = null)
    {
        if (string.IsNullOrWhiteSpace(status))
            return new WorkflowLifecycleResult(false, "invalid_status");

        var updated = await _workflows.UpdateInstanceAsync(instanceId, inst =>
        {
            inst.Status = status;
            inst.Variables = MergeVariables(inst.Variables, variables);
            // Finding 018 — caller-supplied step replaces CurrentActivity so
            // the dashboard "current step" tile updates per worker poll.
            if (!string.IsNullOrWhiteSpace(currentActivity))
                inst.CurrentActivity = currentActivity;
        });

        if (updated is null)
        {
            _logger.LogWarning("UpdateStatusAsync: workflow instance {InstanceId} not found", instanceId);
            return new WorkflowLifecycleResult(false, "not_found");
        }

        _logger.LogInformation(
            "Workflow {InstanceId} status={Status} step={Step}",
            instanceId, status, currentActivity);
        return new WorkflowLifecycleResult(true, null);
    }

    public async Task<WorkflowLifecycleResult> RecordResultAsync(
        Guid instanceId,
        JsonElement result,
        string terminalStatus)
    {
        // Finding 019: validate the three-way state up front so cancelled
        // workflows are never misclassified as failed.
        var normalised = (terminalStatus ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised is not ("completed" or "failed" or "cancelled"))
        {
            return new WorkflowLifecycleResult(false, "invalid_status");
        }

        var resultJson = result.ValueKind switch
        {
            JsonValueKind.Undefined => "null",
            JsonValueKind.Null => "null",
            _ => result.GetRawText()
        };

        var updated = await _workflows.UpdateInstanceAsync(instanceId, inst =>
        {
            inst.Status = normalised;
            inst.CompletedAt = DateTime.UtcNow;
            inst.Result = resultJson;
        });

        if (updated is null)
        {
            _logger.LogWarning("RecordResultAsync: workflow instance {InstanceId} not found", instanceId);
            return new WorkflowLifecycleResult(false, "not_found");
        }

        var eventType = normalised switch
        {
            "completed" => "WORKFLOW.COMPLETED",
            "cancelled" => "WORKFLOW.CANCELLED",
            _ => "WORKFLOW.FAILED"
        };

        await _events.AppendAsync(new DomainEvent
        {
            Type = eventType,
            TenantId = updated.TenantId,
            Tags = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["eventSource"] = "system",
                ["instanceId"] = instanceId.ToString(),
                ["definitionId"] = updated.DefinitionId.ToString()
            }),
            Metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["eventSource"] = "system",
                ["workflowVersion"] = "1.0.0"
            }),
            Data = BuildResultEventData(instanceId, updated, resultJson, normalised)
        });

        _logger.LogInformation(
            "Workflow {InstanceId} terminal status={Status}", instanceId, normalised);
        return new WorkflowLifecycleResult(true, null);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string MergeVariables(string existingJson, JsonElement? incoming)
    {
        // Parse existing JSONB (default "{}" shape) into a mutable dictionary.
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        merged[prop.Name] = prop.Value.Clone();
                }
            }
            catch (JsonException)
            {
                // Preserve non-JSON legacy payloads by dropping them silently.
            }
        }

        if (incoming is JsonElement inc && inc.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in inc.EnumerateObject())
                merged[prop.Name] = prop.Value.Clone();
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var kv in merged)
            {
                writer.WritePropertyName(kv.Key);
                kv.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildResultEventData(
        Guid instanceId, WorkflowInstance instance, string resultJson, string terminalStatus)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("instanceId", instanceId.ToString());
            writer.WriteString("definitionId", instance.DefinitionId.ToString());
            // Both for backward-compat and for SLA dashboards: emit both the
            // tri-state status and the boolean equivalent so consumers don't
            // have to pivot. (Finding 019.)
            writer.WriteString("status", terminalStatus);
            writer.WriteBoolean("success",
                string.Equals(terminalStatus, "completed", StringComparison.OrdinalIgnoreCase));
            writer.WritePropertyName("result");
            using (var resDoc = JsonDocument.Parse(resultJson))
            {
                resDoc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
