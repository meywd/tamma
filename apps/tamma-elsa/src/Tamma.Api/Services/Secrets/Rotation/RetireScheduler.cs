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
///     so the drainer filters on payload content.</description></item>
///   <item><description><see cref="SweepDueRetireTasksAsync"/>
///     drains all pending <c>RETIRE_SECRET_VERSION</c> rows whose
///     payload <c>runAfter</c> is in the past via the shared
///     <see cref="IRetireTaskExecutor"/>. Idempotent: an
///     already-revoked version is a no-op.</description></item>
/// </list>
///
/// <para><b>Two drainers, one body</b>: the AC8-specified route is the
/// per-task <c>RetireSecretVersionTaskHandler</c> driven by
/// <c>PlatformTaskWorker</c>; this sweeper is a periodic fallback. Both
/// call <see cref="IRetireTaskExecutor.RetireOneAsync"/> so the
/// retire-and-revoke behaviour cannot drift between the two paths.</para>
/// </summary>
public sealed class RetireScheduler : IRetireScheduler
{
    public const string TaskType = "RETIRE_SECRET_VERSION";

    private readonly IServiceProvider _services;
    private readonly IRetireTaskExecutor _executor;
    private readonly ILogger<RetireScheduler> _logger;

    public RetireScheduler(
        IServiceProvider services,
        IRetireTaskExecutor executor,
        ILogger<RetireScheduler> logger)
    {
        _services = services;
        _executor = executor;
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
            // Story 29-6 (review fix) — the grace window is enforced by the
            // QUEUE, not by a per-task throw. Setting VisibleAt = runAfter
            // means ReserveNextAsync simply won't claim this row until the
            // window opens, so a not-yet-due retire is NEVER dead-lettered
            // (the old per-task "ordinary throw" path burned the retry budget
            // ~every poll and killed the row in ~25s). The payload still
            // carries RunAfter as the defence-in-depth check inside the
            // executor / sweeper.
            VisibleAt = runAfter.UtcDateTime,
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

            if (parsed is null || parsed.SecretId == Guid.Empty || parsed.VersionNumber <= 0)
            {
                await repo.DeadLetterAsync(reserved.Id, "malformed_payload", ct).ConfigureAwait(false);
                continue;
            }

            if (parsed.RunAfter > DateTimeOffset.UtcNow)
            {
                // Not due yet — DEFER (return to pending with VisibleAt =
                // runAfter, retry count UNCHANGED) instead of FailAsync. With
                // the VisibleAt reservation guard the sweeper won't even
                // reserve a future row, so this is belt-and-suspenders for a
                // clock-skew edge; deferring keeps it from burning the budget.
                await repo.DeferAsync(reserved.Id, parsed.RunAfter.UtcDateTime, ct)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                await _executor.RetireOneAsync(parsed, reserved.Id, ct).ConfigureAwait(false);
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
}
