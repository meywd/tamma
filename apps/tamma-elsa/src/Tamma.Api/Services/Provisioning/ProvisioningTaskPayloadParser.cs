using System.Text.Json;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Shared deserialise-and-guard for the Cranl platform-queue handlers
/// (<see cref="CranlProvisionPlatformTaskHandler"/> /
/// <see cref="CranlDeprovisionPlatformTaskHandler"/>). Centralises the
/// "malformed / empty payload is terminal" contract so both handlers
/// behave identically.
/// </summary>
internal static class ProvisioningTaskPayloadParser
{
    /// <summary>
    /// Deserialise <see cref="PlatformQueuedTask.Payload"/> into a
    /// <see cref="ProvisioningTaskPayload"/>. Throws
    /// <see cref="PlatformTaskTerminalException"/> (non-retryable →
    /// dead-letter) on malformed JSON, a missing payload, or an empty
    /// <see cref="ProvisioningTaskPayload.TenantId"/> — none of those can
    /// succeed on a retry.
    /// </summary>
    public static ProvisioningTaskPayload ParseOrThrow(PlatformQueuedTask task)
    {
        ProvisioningTaskPayload? payload;
        try
        {
            payload = string.IsNullOrEmpty(task.Payload)
                ? null
                : JsonSerializer.Deserialize<ProvisioningTaskPayload>(task.Payload);
        }
        catch (JsonException ex)
        {
            throw new PlatformTaskTerminalException(
                $"provisioning task {task.Id} ({task.Type}) has malformed JSON payload: {ex.Message}",
                ex);
        }

        if (payload is null)
        {
            throw new PlatformTaskTerminalException(
                $"provisioning task {task.Id} ({task.Type}) has no payload " +
                "(expected ProvisioningTaskPayload).");
        }
        if (payload.TenantId == Guid.Empty)
        {
            throw new PlatformTaskTerminalException(
                $"provisioning task {task.Id} ({task.Type}) payload has empty TenantId.");
        }

        return payload;
    }
}
