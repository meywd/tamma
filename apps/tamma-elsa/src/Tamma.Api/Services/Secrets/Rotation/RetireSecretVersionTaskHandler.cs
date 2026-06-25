using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Secrets.Rotation;

/// <summary>
/// Story 29-6 AC8 — the <c>PlatformTaskWorker</c> handler that finally
/// drains the rotation saga's retire tail. Registered via
/// <c>services.AddPlatformTaskHandler&lt;RetireSecretVersionTaskHandler&gt;()</c>;
/// the worker routes <c>RETIRE_SECRET_VERSION</c> rows here.
///
/// <para><b>Why this exists</b>: before this handler, no
/// <see cref="IPlatformTaskHandler"/> was registered for
/// <c>RETIRE_SECRET_VERSION</c>, so a type-blind worker would observe a
/// no-handler tick on every reserved retire row and (after the retry
/// ceiling) dead-letter it — destroying a scheduled secret retirement
/// before its grace period. Registering this handler makes the type
/// HANDLED, which is the fix. (We deliberately do NOT flip
/// <c>PlatformTaskWorker:RunOnStartup</c> as part of this change.)</para>
///
/// <para><b>Failure semantics</b> (delegated to
/// <see cref="PlatformTaskWorker.ProcessOnceAsync"/> via return / throw):</para>
/// <list type="bullet">
///   <item><description>malformed / empty payload ⇒
///     <see cref="PlatformTaskTerminalException"/> (<c>malformed_payload</c>)
///     so the row goes straight to dead-letter without burning the
///     retry budget.</description></item>
///   <item><description><c>RunAfter &gt; now</c> ⇒ the row is DEFERRED
///     (<see cref="IPlatformQueuedTaskRepository.DeferAsync"/>): returned
///     to <c>pending</c> with <c>VisibleAt = runAfter</c> and the retry
///     count LEFT UNCHANGED, so it is never dead-lettered before its grace
///     window. This path is now belt-and-suspenders — the primary defence
///     is that <c>ReserveNextAsync</c> won't claim a future-<c>VisibleAt</c>
///     row at all (the scheduler stamps <c>VisibleAt = runAfter</c> at
///     enqueue); it only fires on a clock-skew edge. The old code threw an
///     ordinary exception so <c>FailAsync</c> re-queued WITH a retry-count
///     bump and NO run-after — which dead-lettered a not-due retire in
///     ~25s. That was the lost-retire bug this fix closes.</description></item>
///   <item><description>gateway retire throw ⇒ an ordinary exception
///     (worker retry budget; dead-letter at the ceiling).</description></item>
///   <item><description>handler <c>RevokeOldAsync</c> throw ⇒ best-effort
///     log+continue (the store-side revoke already happened); the row
///     still completes.</description></item>
///   <item><description>already-<c>Revoked</c> version ⇒ idempotent
///     no-op; the row completes.</description></item>
/// </list>
/// </summary>
public sealed class RetireSecretVersionTaskHandler : IPlatformTaskHandler
{
    private readonly IRetireTaskExecutor _executor;
    private readonly IPlatformQueuedTaskRepository _repo;
    private readonly ILogger<RetireSecretVersionTaskHandler> _logger;

    public RetireSecretVersionTaskHandler(
        IRetireTaskExecutor executor,
        IPlatformQueuedTaskRepository repo,
        ILogger<RetireSecretVersionTaskHandler> logger)
    {
        _executor = executor;
        _repo = repo;
        _logger = logger;
    }

    public string TaskType => RetireScheduler.TaskType;

    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        RetireTaskPayload? payload;
        try
        {
            payload = string.IsNullOrWhiteSpace(task.Payload)
                ? null
                : JsonSerializer.Deserialize<RetireTaskPayload>(task.Payload);
        }
        catch (JsonException ex)
        {
            // Will never parse — terminal so the worker dead-letters
            // immediately (no retry budget burned).
            throw new PlatformTaskTerminalException(
                $"malformed_payload: retire task {task.Id} has unparseable JSON: {ex.Message}",
                ex);
        }

        if (payload is null || payload.SecretId == Guid.Empty || payload.VersionNumber <= 0)
        {
            throw new PlatformTaskTerminalException(
                $"malformed_payload: retire task {task.Id} payload is empty or missing secretId/versionNumber.");
        }

        if (payload.RunAfter > DateTimeOffset.UtcNow)
        {
            // Not due yet — DEFER, do not fail. Return the row to 'pending'
            // with VisibleAt = runAfter and the retry count UNCHANGED, then
            // throw PlatformTaskDeferredException so the worker treats it as
            // a no-op (no CompleteAsync clobber, no FailAsync budget burn).
            //
            // Under normal operation this path is unreachable: the scheduler
            // stamps VisibleAt = runAfter at enqueue, so ReserveNextAsync
            // won't claim a future row at all. This belt-and-suspenders only
            // fires on a clock-skew edge (row reserved a hair before its
            // window). The OLD code threw an ordinary exception → FailAsync
            // bumped RetryCount with no run-after → the row was re-delivered
            // every ~5s poll and dead-lettered in ~25s, destroying the
            // scheduled retirement before its grace. THAT is the bug closed.
            _logger.LogDebug(
                "secret.retire.not_yet_due taskId={TaskId} secret={Secret} v{Version} runAfter={RunAfter:o}",
                task.Id, payload.SecretId, payload.VersionNumber, payload.RunAfter);
            await _repo.DeferAsync(task.Id, payload.RunAfter.UtcDateTime, ct).ConfigureAwait(false);
            throw new PlatformTaskDeferredException(
                $"not_yet_due: retire task {task.Id} deferred until {payload.RunAfter:o}.");
        }

        try
        {
            // Idempotent body (shared with the periodic sweeper). A
            // throwing RevokeOld is swallowed inside the executor; a
            // gateway throw propagates here as a retryable failure.
            await _executor.RetireOneAsync(payload, task.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutdown — leave the row in 'processing' for the
            // reaper. RetireVersionAsync is idempotent so the re-run is
            // safe.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "secret.retire.failed taskId={TaskId} secret={Secret} v{Version}",
                task.Id, payload.SecretId, payload.VersionNumber);
            // Ordinary throw — worker FailAsync (retry budget, dead-letter
            // at the ceiling).
            throw;
        }

        _logger.LogInformation(
            "secret.retire.completed taskId={TaskId} secret={Secret} v{Version}",
            task.Id, payload.SecretId, payload.VersionNumber);
    }
}
