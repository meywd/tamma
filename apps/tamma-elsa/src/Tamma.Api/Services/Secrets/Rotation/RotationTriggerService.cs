using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 (audit gap #2 + #3) — default
/// <see cref="IRotationTriggerService"/>. Bridges a rotation trigger
/// (operator endpoint or scheduled auto-rotation) to the
/// <c>rotate-secret</c> Elsa workflow over the existing HTTP dispatch
/// seam (<see cref="IElsaWorkflowService"/>), with the per-secret
/// concurrency guard in front.
///
/// <para><b>No empty/plain fallback</b>: this service does NOT pre-resolve
/// the handler or skip secrets with no consumer. A secret with no handler
/// is dispatched anyway and the saga emits
/// <c>SECRET.ROTATION.FAILED(handler_not_registered)</c> — an honest,
/// audited failure rather than a silent skip.</para>
/// </summary>
public sealed class RotationTriggerService : IRotationTriggerService
{
    /// <summary>Workflow definition id of <c>RotateSecretWorkflow</c>.</summary>
    public const string WorkflowDefinitionId = "rotate-secret";

    private readonly ISecretRotationGateway _gateway;
    private readonly IRotationAuditEmitter _auditor;
    private readonly IElsaWorkflowService _workflows;
    private readonly ILogger<RotationTriggerService> _logger;

    public RotationTriggerService(
        ISecretRotationGateway gateway,
        IRotationAuditEmitter auditor,
        IElsaWorkflowService workflows,
        ILogger<RotationTriggerService> logger)
    {
        _gateway = gateway;
        _auditor = auditor;
        _workflows = workflows;
        _logger = logger;
    }

    public async Task<RotationTriggerResult> TriggerRotationAsync(
        Guid secretId,
        Guid operatorUserId,
        string? newPlaintext,
        int? generateLength,
        long graceWindowSeconds,
        CancellationToken ct)
    {
        if (secretId == Guid.Empty)
            throw new ArgumentException("secretId must be a non-empty Guid", nameof(secretId));

        var correlationId = $"rot_{Guid.NewGuid():N}";

        // Resolve the secret's tenant scope for audit tagging. A
        // not-found secret is still dispatched — the saga short-circuits
        // to SECRET.ROTATION.FAILED(secret_not_found), an honest failure.
        var snapshot = await _gateway.GetSnapshotAsync(secretId, ct).ConfigureAwait(false);
        var tenantId = snapshot?.TenantId;

        // #3 — per-secret concurrency guard. Reject (do NOT dispatch) when
        // a rotation is already in flight.
        var acquired = await _gateway
            .TryBeginRotationAsync(secretId, correlationId, ct)
            .ConfigureAwait(false);
        if (!acquired)
        {
            await _auditor.EmitAsync(
                RotationAuditEvent.Create(
                    RotationAuditEvents.Rejected,
                    secretId,
                    tenantId,
                    correlationId,
                    detail: "rotation_in_progress"),
                ct).ConfigureAwait(false);
            _logger.LogWarning(
                "secret.rotation.rejected secret={Secret} reason=rotation_in_progress", secretId);
            return new RotationTriggerResult(
                Accepted: false, correlationId, Reason: "rotation_in_progress");
        }

        var input = new Dictionary<string, object>
        {
            ["secretId"] = secretId.ToString(),
            ["rotationCorrelationId"] = correlationId,
            ["operatorUserId"] = operatorUserId.ToString(),
            ["graceWindowSeconds"] = graceWindowSeconds,
        };
        if (!string.IsNullOrEmpty(newPlaintext))
            input["newPlaintext"] = newPlaintext;
        else if (generateLength is > 0)
            input["generateLength"] = generateLength.Value;

        try
        {
            await _workflows.StartWorkflowAsync(WorkflowDefinitionId, input).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispatch failed before the saga minted a pending version —
            // release the claim so a retry isn't blocked by a phantom
            // in-flight marker. (No-op for the status-check backend, but
            // correct for advisory-lock backends.)
            await _gateway.EndRotationAsync(secretId, correlationId, ct).ConfigureAwait(false);
            throw;
        }

        await _auditor.EmitAsync(
            RotationAuditEvent.Create(
                RotationAuditEvents.Requested,
                secretId,
                tenantId,
                correlationId,
                data: new Dictionary<string, object?>
                {
                    ["operatorUserId"] = operatorUserId,
                    ["graceWindowSeconds"] = graceWindowSeconds,
                    // NEVER the plaintext — only whether one was supplied.
                    ["generated"] = string.IsNullOrEmpty(newPlaintext),
                }),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "secret.rotation.requested secret={Secret} corr={Corr} operator={Operator}",
            secretId, correlationId, operatorUserId);

        return new RotationTriggerResult(Accepted: true, correlationId, Reason: null);
    }
}
