using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Integrations;
using Tamma.Core.Interfaces;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Jira;

/// <summary>
/// Story 38 (Phase 1) + integration BYOK — composes the JIRA-mediation sequence
/// entirely inside <c>Tamma.Api</c>: resolve the acting tenant's JIRA credential
/// per-request (BYOK→system→fail-loud, like git/LLM), thread it into the
/// credential-bound <see cref="IJiraApiClient"/>, then emit exactly one terminal
/// DCB event scoped by the tenant. The JIRA token NEVER reaches the engine — it
/// stays in Tamma.Api (cabinet or single-user config).
///
/// <para><b>Fail-loud tenant resolution (replaces the old SaaS-deny guard).</b>
/// The credential is resolved via <see cref="IJiraCredentialResolver"/>:
/// <list type="bullet">
///   <item><b>present</b> — the tenant's BYOK bundle (SaaS) or the single-user
///     <c>Jira:*</c> config (system tier) ⇒ ALLOW, using THAT credential.</item>
///   <item><b>absent</b> — no per-tenant credential and no legitimate system tier
///     ⇒ <b>fail loud</b> with the typed key-free
///     <see cref="JiraFailureCodes.CredentialUnavailable"/> and a WARN log; the
///     JIRA client is NEVER reached. This is the confused-deputy fix: SaaS no
///     longer silently falls back to a shared platform credential.</item>
/// </list></para>
/// </summary>
public sealed class JiraMediationService : IJiraMediationService
{
    private readonly IJiraApiClient _jira;
    private readonly IJiraCredentialResolver _credentials;
    private readonly IEventRepository _events;
    private readonly ILogger<JiraMediationService> _logger;

    public JiraMediationService(
        IJiraApiClient jira,
        IJiraCredentialResolver credentials,
        IEventRepository events,
        ILogger<JiraMediationService> logger)
    {
        _jira = jira ?? throw new ArgumentNullException(nameof(jira));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<JiraMediationResult> GetTicketAsync(Guid? tenantId, string ticketId, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, ticketId, JiraEventTypes.TicketReadOperation, JiraEventTypes.TicketReadFailed, correlationId, ct,
            cred => GetTicketCoreAsync(tenantId, ticketId, correlationId, cred, ct));

    public Task<JiraMediationResult> UpdateTicketAsync(Guid? tenantId, string ticketId, UpdateTicketRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, ticketId, JiraEventTypes.TicketUpdateOperation, JiraEventTypes.TicketUpdatedFailed, body.CorrelationId, ct,
            cred => UpdateTicketCoreAsync(tenantId, ticketId, body, cred, ct));
    }

    // ===================================================================
    // Read ticket
    // ===================================================================

    private async Task<JiraMediationResult> GetTicketCoreAsync(Guid? tenantId, string ticketId, string correlationId, JiraCredential credential, CancellationToken ct)
    {
        var op = JiraEventTypes.TicketReadOperation;
        var res = await _jira.GetTicketAsync(credential, ticketId, ct).ConfigureAwait(false);

        if (!res.Success)
            return await FailAsync(tenantId, ticketId, op, JiraEventTypes.TicketReadFailed, correlationId, res.Error, ct).ConfigureAwait(false);

        var t = res.Data;
        var ticket = t is null ? null : new JiraTicketDto
        {
            Id = t.Id,
            Key = t.Key,
            Summary = t.Summary,
            Description = t.Description,
            Status = t.Status,
            Assignee = t.Assignee,
            Priority = t.Priority,
            Labels = t.Labels.ToList(),
        };

        var ok = new JiraMediationResult
        {
            Success = true,
            Outcome = "Read",
            Ticket = ticket,
            TicketKey = ticket?.Key ?? ticketId,
            CorrelationId = correlationId,
        };
        await EmitAsync(JiraEventTypes.TicketReadSuccess, op, tenantId, ticketId, correlationId, null,
            new { ticketId, found = ticket is not null }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Update ticket (status transition + comment)
    // ===================================================================

    private async Task<JiraMediationResult> UpdateTicketCoreAsync(Guid? tenantId, string ticketId, UpdateTicketRequest body, JiraCredential credential, CancellationToken ct)
    {
        var op = JiraEventTypes.TicketUpdateOperation;
        var update = new JiraTicketUpdate
        {
            Status = body.Status,
            Comment = body.Comment,
            CustomFields = body.CustomFields,
        };

        var res = await _jira.UpdateTicketAsync(credential, ticketId, update, ct).ConfigureAwait(false);

        if (!res.Success)
            return await FailAsync(tenantId, ticketId, op, JiraEventTypes.TicketUpdatedFailed, correlationId: body.CorrelationId, res.Error, ct).ConfigureAwait(false);

        var ok = new JiraMediationResult
        {
            Success = true,
            Outcome = "Updated",
            TicketKey = res.Data?.TicketKey ?? ticketId,
            CorrelationId = body.CorrelationId,
        };
        await EmitAsync(JiraEventTypes.TicketUpdatedSuccess, op, tenantId, ticketId, body.CorrelationId, null,
            new { ticketId, status = body.Status }, ct).ConfigureAwait(false);
        return ok;
    }

    // ===================================================================
    // Failure / guarded-envelope shared paths
    // ===================================================================

    private async Task<JiraMediationResult> FailAsync(
        Guid? tenantId, string ticketId, string operation, string failedEventType, string correlationId, string? reason, CancellationToken ct)
    {
        var failCode = MapFailure(reason);
        var fail = new JiraMediationResult
        {
            Success = false,
            Outcome = "Error",
            FailureCode = failCode,
            FailureReason = reason,
            TicketKey = ticketId,
            CorrelationId = correlationId,
        };
        await EmitAsync(failedEventType, operation, tenantId, ticketId, correlationId, failCode, new { ticketId }, ct).ConfigureAwait(false);
        return fail;
    }

    private async Task<JiraMediationResult> ExecuteGuardedAsync(
        Guid? tenantId, string ticketId, string operation, string failedEventType, string correlationId,
        CancellationToken ct, Func<JiraCredential, Task<JiraMediationResult>> body)
    {
        // Fail-loud per-tenant credential resolution (runs BEFORE the body so the
        // JIRA client is never reached on an unresolved credential). In SaaS the
        // tenant must have registered its own BYOK bundle; single-user resolves
        // the Jira:* config as the system tier. Absent ⇒ typed key-free failure,
        // NOT a silent shared-credential fallback.
        JiraCredentialResolution? resolution;
        try
        {
            resolution = await _credentials.ResolveAsync(tenantId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "jira-mediation credential resolution threw for op {Operation}; failing loud (CREDENTIAL_UNAVAILABLE). correlationId={CorrelationId}, ticketId={TicketId}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(ticketId), tenantId);
            resolution = null;
        }

        if (resolution is null)
        {
            _logger.LogWarning(
                "jira-mediation FAILED-LOUD (no JIRA credential for tenant): op {Operation} refused — register a per-tenant JIRA credential or configure Jira:* (single-user). correlationId={CorrelationId}, ticketId={TicketId}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(ticketId), tenantId);

            await EmitAsync(failedEventType, operation, tenantId, ticketId, correlationId,
                JiraFailureCodes.CredentialUnavailable, new { ticketId }, ct).ConfigureAwait(false);

            return new JiraMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = JiraFailureCodes.CredentialUnavailable,
                FailureReason = "no JIRA credential is configured for this tenant; register one via POST /api/v1/integrations/jira/credential.",
                TicketKey = ticketId,
                CorrelationId = correlationId,
            };
        }

        try
        {
            return await body(resolution.Credential).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "jira-mediation op {Operation} threw; returning typed PLATFORM_ERROR (never a raw 5xx) with one FAILED event. correlationId={CorrelationId}, ticketId={TicketId}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(ticketId), tenantId);

            await EmitAsync(failedEventType, operation, tenantId, ticketId, correlationId, JiraFailureCodes.PlatformError, new { }, ct).ConfigureAwait(false);

            return new JiraMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = JiraFailureCodes.PlatformError,
                FailureReason = "an unexpected error occurred processing the JIRA operation",
                TicketKey = ticketId,
                CorrelationId = correlationId,
            };
        }
    }

    // ===================================================================
    // DCB audit (exactly one terminal JIRA.* event per call)
    // ===================================================================

    private async Task EmitAsync(
        string eventType, string operation, Guid? tenantId, string ticketId, string correlationId,
        string? failureCode, object data, CancellationToken ct)
    {
        try
        {
            object tagsObj = failureCode is null
                ? new { tenantId = tenantId?.ToString(), ticketId, operation, correlationId }
                : new { tenantId = tenantId?.ToString(), ticketId, operation, correlationId, failureCode };

            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = eventType,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(tagsObj),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(data),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "JIRA.* event append failed (type={Type}); the mediation result still returns. correlationId={CorrelationId}, ticketId={TicketId}, tenantId={TenantId}",
                eventType, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(ticketId), tenantId);
        }
    }

    private static string MapFailure(string? reason)
    {
        var r = (reason ?? string.Empty).ToLowerInvariant();
        if (r.Contains("not configured")) return JiraFailureCodes.NotConfigured;
        if (r.Contains("404") || r.Contains("not found")) return JiraFailureCodes.NotFound;
        return JiraFailureCodes.PlatformError;
    }
}
