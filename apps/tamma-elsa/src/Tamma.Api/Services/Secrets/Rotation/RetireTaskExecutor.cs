using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC8 — default <see cref="IRetireTaskExecutor"/>. Holds the
/// single-version retire body shared by the per-task
/// <c>RetireSecretVersionTaskHandler</c> and the periodic
/// <see cref="RetireScheduler.SweepDueRetireTasksAsync"/> fallback.
/// </summary>
public sealed class RetireTaskExecutor : IRetireTaskExecutor
{
    private readonly ISecretRotationGateway _gateway;
    private readonly IRotationHandlerRegistry _registry;
    private readonly IRotationAuditEmitter _auditor;
    private readonly ILogger<RetireTaskExecutor> _logger;

    public RetireTaskExecutor(
        ISecretRotationGateway gateway,
        IRotationHandlerRegistry registry,
        IRotationAuditEmitter auditor,
        ILogger<RetireTaskExecutor> logger)
    {
        _gateway = gateway;
        _registry = registry;
        _auditor = auditor;
        _logger = logger;
    }

    public async Task RetireOneAsync(
        RetireTaskPayload payload, Guid taskId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Fetch the old plaintext BEFORE retiring so the handler's
        // RevokeOld can use it (e.g. Postgres can ALTER ROLE with the
        // last-known password). After RetireVersionAsync scrubs the
        // ciphertext this read would return null.
        var oldPlaintext = await _gateway
            .GetVersionPlaintextAsync(payload.SecretId, payload.VersionNumber, ct)
            .ConfigureAwait(false);

        // Idempotent: an already-revoked version is a no-op inside the
        // gateway. This is the AC8 "idempotent on already-Revoked" edge.
        await _gateway
            .RetireVersionAsync(payload.SecretId, payload.VersionNumber, ct)
            .ConfigureAwait(false);

        var snapshot = await _gateway
            .GetSnapshotAsync(payload.SecretId, ct)
            .ConfigureAwait(false);

        if (snapshot is not null && oldPlaintext is not null)
        {
            var handler = _registry.Resolve(snapshot.ConsumerSystem);
            if (handler is not null)
            {
                try
                {
                    var target = new RotationTarget(
                        snapshot.SecretId,
                        snapshot.Name,
                        snapshot.TenantId,
                        snapshot.ConsumerSystem,
                        snapshot.ConsumerIdentifier,
                        NewVersionNumber: snapshot.ActiveVersionNumber,
                        PreviousVersionNumber: payload.VersionNumber);
                    await handler
                        .RevokeOldAsync(
                            target,
                            oldPlaintext,
                            new RotationContext(
                                payload.RotationCorrelationId,
                                Guid.Empty,
                                DryRun: false,
                                new Dictionary<string, string>()),
                            ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Best-effort: the version is already Revoked in the
                    // store; a handler-cleanup hiccup must not undo that
                    // or fail the queue row.
                    _logger.LogWarning(ex,
                        "RevokeOldAsync threw for secret {Secret} v{Version}; " +
                        "version is still revoked in the store but the handler's " +
                        "cleanup did not complete.",
                        payload.SecretId, payload.VersionNumber);
                }
            }
        }

        await _auditor
            .EmitAsync(
                RotationAuditEvent.Create(
                    RotationAuditEvents.VersionRetired,
                    payload.SecretId,
                    snapshot?.TenantId,
                    payload.RotationCorrelationId,
                    versionNumber: payload.VersionNumber,
                    data: new Dictionary<string, object?>
                    {
                        ["taskId"] = taskId,
                        ["versionNumber"] = payload.VersionNumber,
                    }),
                ct)
            .ConfigureAwait(false);
    }
}
