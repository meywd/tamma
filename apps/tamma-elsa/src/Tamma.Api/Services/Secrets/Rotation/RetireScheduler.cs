using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC2 step 6 + AC8 — production
/// <see cref="IRetireScheduler"/> that:
///
/// <list type="bullet">
///   <item><description><see cref="ScheduleRetireAsync"/> persists a
///     <c>platform_queued_tasks</c> row with type
///     <c>RETIRE_SECRET_VERSION</c> and a JSON payload carrying the
///     secret id, version number, <c>run_after</c> ISO-8601 timestamp,
///     and the rotation correlation id. <c>PlatformQueuedTask</c> in
///     28-10 does not yet model a first-class <c>RunAfter</c> column,
///     so the sweeper filters on payload content.</description></item>
///   <item><description><see cref="SweepDueRetireTasksAsync"/>
///     drains all pending <c>RETIRE_SECRET_VERSION</c> rows whose
///     payload <c>runAfter</c> is in the past, calls the gateway's
///     <see cref="ISecretRotationGateway.RetireVersionAsync"/>, and
///     invokes the handler's optional <c>RevokeOldAsync</c> hook.
///     Idempotent: an already-revoked version is a no-op.</description></item>
/// </list>
/// </summary>
public sealed class RetireScheduler : IRetireScheduler
{
    public const string TaskType = "RETIRE_SECRET_VERSION";

    private readonly IServiceProvider _services;
    private readonly ISecretRotationGateway _gateway;
    private readonly IRotationHandlerRegistry _registry;
    private readonly IRotationAuditEmitter _auditor;
    private readonly ILogger<RetireScheduler> _logger;

    public RetireScheduler(
        IServiceProvider services,
        ISecretRotationGateway gateway,
        IRotationHandlerRegistry registry,
        IRotationAuditEmitter auditor,
        ILogger<RetireScheduler> logger)
    {
        _services = services;
        _gateway = gateway;
        _registry = registry;
        _auditor = auditor;
        _logger = logger;
    }

    public async Task<Guid> ScheduleRetireAsync(
        Guid secretId,
        int versionNumber,
        Guid? tenantId,
        DateTimeOffset runAfter,
        string rotationCorrelationId,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();

        var payload = JsonSerializer.Serialize(new RetireTaskPayload
        {
            SecretId = secretId,
            VersionNumber = versionNumber,
            RunAfter = runAfter,
            RotationCorrelationId = rotationCorrelationId,
        });

        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = TaskType,
            TenantId = tenantId,
            Payload = payload,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var enqueued = await repo.EnqueueAsync(task, ct).ConfigureAwait(false);
        return enqueued.Id;
    }

    public async Task<int> SweepDueRetireTasksAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlatformQueuedTaskRepository>();

        var processed = 0;
        while (true)
        {
            var reserved = await repo.ReserveNextAsync("rotation-sweeper", ct).ConfigureAwait(false);
            if (reserved is null) break;

            if (reserved.Type != TaskType)
            {
                // Not ours — release back by failing with a retryable
                // error so another worker can pick it up.
                await repo.FailAsync(reserved.Id, "wrong_type_for_sweeper", maxRetries: int.MaxValue, ct)
                    .ConfigureAwait(false);
                continue;
            }

            RetireTaskPayload? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<RetireTaskPayload>(reserved.Payload);
            }
            catch (JsonException) { /* fall through to dead-letter */ }

            if (parsed is null)
            {
                await repo.DeadLetterAsync(reserved.Id, "malformed_payload", ct).ConfigureAwait(false);
                continue;
            }

            if (parsed.RunAfter > DateTimeOffset.UtcNow)
            {
                // Not due yet — put back.
                await repo.FailAsync(reserved.Id, "not_yet_due", maxRetries: int.MaxValue, ct)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                // Fetch the old plaintext BEFORE retiring so the handler's
                // RevokeOld can use it (e.g. Postgres can ALTER ROLE with
                // the last-known password).
                var oldPlaintext = await _gateway.GetVersionPlaintextAsync(
                        parsed.SecretId, parsed.VersionNumber, ct)
                    .ConfigureAwait(false);

                await _gateway.RetireVersionAsync(parsed.SecretId, parsed.VersionNumber, ct)
                    .ConfigureAwait(false);

                var snapshot = await _gateway.GetSnapshotAsync(parsed.SecretId, ct).ConfigureAwait(false);
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
                                PreviousVersionNumber: parsed.VersionNumber);
                            await handler.RevokeOldAsync(
                                    target,
                                    oldPlaintext,
                                    new RotationContext(
                                        parsed.RotationCorrelationId,
                                        Guid.Empty,
                                        DryRun: false,
                                        new Dictionary<string, string>()),
                                    ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "RevokeOldAsync threw for secret {Secret} v{Version}; " +
                                "version is still revoked in the store but the handler's " +
                                "cleanup did not complete.",
                                parsed.SecretId, parsed.VersionNumber);
                        }
                    }
                }

                await _auditor.EmitAsync(
                    RotationAuditEvent.Create(
                        RotationAuditEvents.VersionRetired,
                        parsed.SecretId,
                        snapshot?.TenantId,
                        parsed.RotationCorrelationId,
                        versionNumber: parsed.VersionNumber,
                        data: new Dictionary<string, object?>
                        {
                            ["taskId"] = reserved.Id,
                        }),
                    ct)
                    .ConfigureAwait(false);

                await repo.CompleteAsync(reserved.Id, ct).ConfigureAwait(false);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Retire sweeper failed for secret {Secret} v{Version}",
                    parsed.SecretId, parsed.VersionNumber);
                await repo.FailAsync(reserved.Id, ex.Message, maxRetries: 3, ct).ConfigureAwait(false);
            }
        }

        return processed;
    }

    internal sealed class RetireTaskPayload
    {
        public Guid SecretId { get; set; }
        public int VersionNumber { get; set; }
        public DateTimeOffset RunAfter { get; set; }
        public string RotationCorrelationId { get; set; } = string.Empty;
    }
}
