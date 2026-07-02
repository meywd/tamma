using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Interfaces;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Jira;

/// <summary>
/// Story 38 (Phase 1) — composes the JIRA-mediation sequence entirely inside
/// <c>Tamma.Api</c>: platform call via the existing config-credentialed
/// <see cref="IJiraIntegrationService"/> (the JIRA base URL / email / API token live
/// in Tamma.Api config, resolved inside that service) → exactly-one terminal DCB
/// event scoped by the acting tenant. Unlike git/CI there is no per-tenant BYOK
/// token resolver: JIRA is a single, server-side, config-provided integration
/// (mirrors the Slack outbox plane, not the repo-scoped git plane). The JIRA token
/// NEVER reaches the engine — it stays in Tamma.Api config.
///
/// <para><b>Fail-closed tenant guard (SaaS).</b> Because that single credential has
/// NO per-tenant/ticket authorization — and there is no tenant↔JIRA-project mapping
/// to derive one from — the shared-credential path is a confused-deputy in SaaS: any
/// tenant A could GET/PATCH ANY ticket id belonging to tenant B through the global
/// client. So this service is mode-gated (mirroring <c>GitRepoAuthorizer</c>):
/// <list type="bullet">
///   <item><b>single-user</b> — the sole principal owns everything ⇒ ALLOW.</item>
///   <item><b>SaaS</b> — DENY by default with a typed, key-free soft-fail
///     (<see cref="JiraFailureCodes.SharedCredentialDeniedInSaaS"/>) and a WARN log;
///     the underlying <see cref="IJiraIntegrationService"/> is NEVER called. An
///     operator may re-enable the shared-credential behavior knowingly by setting
///     <c>Jira:AllowSharedCredentialInSaaS=true</c> (default <c>false</c>).</item>
/// </list>
/// This is a conservative guard, not full per-tenant JIRA scoping (blocked until a
/// tenant↔project mapping exists).</para>
/// </summary>
public sealed class JiraMediationService : IJiraMediationService
{
    private readonly IJiraIntegrationService _jira;
    private readonly IEventRepository _events;
    private readonly ITammaModeProvider _mode;
    private readonly bool _allowSharedCredentialInSaaS;
    private readonly ILogger<JiraMediationService> _logger;

    public JiraMediationService(
        IJiraIntegrationService jira,
        IEventRepository events,
        ITammaModeProvider mode,
        IConfiguration configuration,
        ILogger<JiraMediationService> logger)
    {
        _jira = jira ?? throw new ArgumentNullException(nameof(jira));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Opt-in escape hatch: an operator who has accepted the cross-tenant risk of
        // a shared JIRA credential in SaaS sets this to true. Default (unset / any
        // non-"true" value) ⇒ false ⇒ SaaS denies.
        _allowSharedCredentialInSaaS =
            bool.TryParse(configuration["Jira:AllowSharedCredentialInSaaS"], out var allow) && allow;
    }

    public Task<JiraMediationResult> GetTicketAsync(Guid? tenantId, string ticketId, string correlationId, CancellationToken ct = default)
        => ExecuteGuardedAsync(tenantId, ticketId, JiraEventTypes.TicketReadOperation, JiraEventTypes.TicketReadFailed, correlationId, ct,
            () => GetTicketCoreAsync(tenantId, ticketId, correlationId, ct));

    public Task<JiraMediationResult> UpdateTicketAsync(Guid? tenantId, string ticketId, UpdateTicketRequest body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteGuardedAsync(tenantId, ticketId, JiraEventTypes.TicketUpdateOperation, JiraEventTypes.TicketUpdatedFailed, body.CorrelationId, ct,
            () => UpdateTicketCoreAsync(tenantId, ticketId, body, ct));
    }

    // ===================================================================
    // Read ticket
    // ===================================================================

    private async Task<JiraMediationResult> GetTicketCoreAsync(Guid? tenantId, string ticketId, string correlationId, CancellationToken ct)
    {
        var op = JiraEventTypes.TicketReadOperation;
        var res = await _jira.GetJiraTicketAsync(ticketId).ConfigureAwait(false);

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

    private async Task<JiraMediationResult> UpdateTicketCoreAsync(Guid? tenantId, string ticketId, UpdateTicketRequest body, CancellationToken ct)
    {
        var op = JiraEventTypes.TicketUpdateOperation;
        var update = new JiraTicketUpdate
        {
            Status = body.Status,
            Comment = body.Comment,
            CustomFields = body.CustomFields,
        };

        var res = await _jira.UpdateJiraTicketAsync(ticketId, update).ConfigureAwait(false);

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
        CancellationToken ct, Func<Task<JiraMediationResult>> body)
    {
        // Fail-closed tenant guard (runs BEFORE the body so the shared-credential
        // IJiraIntegrationService is never reached on denial). In SaaS the single
        // platform-global JIRA credential has no per-tenant/ticket scoping ⇒ deny
        // unless an operator opted in. Single-user owns everything ⇒ allow.
        if (_mode.Mode == TammaMode.SaaS && !_allowSharedCredentialInSaaS)
        {
            _logger.LogWarning(
                "jira-mediation guard DENIED (shared-credential in SaaS): op {Operation} refused — JIRA has no per-tenant scoping and Jira:AllowSharedCredentialInSaaS is not set. correlationId={CorrelationId}, ticketId={TicketId}, tenantId={TenantId}",
                operation, LogSanitizer.Clean(correlationId), LogSanitizer.Clean(ticketId), tenantId);

            await EmitAsync(failedEventType, operation, tenantId, ticketId, correlationId,
                JiraFailureCodes.SharedCredentialDeniedInSaaS, new { ticketId }, ct).ConfigureAwait(false);

            return new JiraMediationResult
            {
                Success = false,
                Outcome = "Error",
                FailureCode = JiraFailureCodes.SharedCredentialDeniedInSaaS,
                FailureReason = "JIRA uses a shared platform credential with no per-tenant scoping; refused in SaaS mode. Set Jira:AllowSharedCredentialInSaaS=true to allow knowingly.",
                TicketKey = ticketId,
                CorrelationId = correlationId,
            };
        }

        try
        {
            return await body().ConfigureAwait(false);
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
