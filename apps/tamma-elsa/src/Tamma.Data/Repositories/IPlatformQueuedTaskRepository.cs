using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Persistence port for the control-plane <see cref="PlatformQueuedTask"/>
/// queue — pre-routing tasks that exist before a tenant DB has been
/// resolved (e.g. raw GitHub installation webhooks, post-tenant-creation
/// fan-out tasks). The CP analogue of
/// <see cref="IQueuedTaskRepository"/>.
///
/// <para>Reservation semantics use Postgres
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c> so multiple workers across
/// API pods may drain the queue concurrently without double-processing
/// any row. The provider-independent fallback (EF InMemory, used by
/// unit tests) does naive find-then-update — safe for a single writer.
/// </para>
/// </summary>
public interface IPlatformQueuedTaskRepository
{
    /// <summary>
    /// Insert a new task in <c>pending</c> state and return the persisted
    /// row. Callers populate <see cref="PlatformQueuedTask.Type"/> and
    /// optional <see cref="PlatformQueuedTask.Payload"/> /
    /// <see cref="PlatformQueuedTask.TenantId"/> /
    /// <see cref="PlatformQueuedTask.InstallationId"/>; everything else is
    /// filled in here.
    /// </summary>
    Task<PlatformQueuedTask> EnqueueAsync(PlatformQueuedTask task, CancellationToken ct = default);

    /// <summary>
    /// Atomically reserve the next <c>pending</c> task and flip it to
    /// <c>processing</c>. Returns the claimed row, or <c>null</c> when
    /// the queue is empty. Concurrent callers each receive a different
    /// row (no double-claim) thanks to <c>FOR UPDATE SKIP LOCKED</c> on
    /// Postgres. <paramref name="workerId"/> is recorded on the row's
    /// (currently shadow) lease bookkeeping for observability — it does
    /// not affect correctness.
    /// </summary>
    Task<PlatformQueuedTask?> ReserveNextAsync(
        string workerId, CancellationToken ct = default);

    /// <summary>Mark a previously-reserved task as <c>completed</c>.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Record a transient failure on a reserved task. Increments
    /// <see cref="PlatformQueuedTask.RetryCount"/> and, when
    /// <paramref name="retryCount"/> remains under
    /// <paramref name="maxRetries"/>, returns the row to <c>pending</c>
    /// state with the supplied <paramref name="error"/> recorded so the
    /// next reservation may pick it up. Beyond the retry ceiling the
    /// row is moved to <c>dead_letter</c> instead.
    /// </summary>
    Task<PlatformQueuedTask?> FailAsync(
        Guid id, string error, int maxRetries, CancellationToken ct = default);

    /// <summary>
    /// Move a task to the <c>dead_letter</c> terminal state with the
    /// supplied <paramref name="error"/> reason. Used by callers that
    /// know the task can never succeed (unknown handler, malformed
    /// payload).
    /// </summary>
    Task DeadLetterAsync(Guid id, string error, CancellationToken ct = default);

    /// <summary>
    /// Round-2 H8 — record a "no handler registered" observation
    /// without dead-lettering the row. Increments
    /// <see cref="PlatformQueuedTask.RetryCount"/>, sets
    /// <see cref="PlatformQueuedTask.UnprocessableAt"/>, and returns the
    /// row to <c>pending</c> so a deploy that subsequently registers
    /// the handler can pick the work up. Falls through to
    /// <c>dead_letter</c> once <see cref="PlatformQueuedTask.RetryCount"/>
    /// reaches <paramref name="maxRetries"/>.
    /// </summary>
    Task<PlatformQueuedTask?> ParkUnprocessableAsync(
        Guid id, string reason, int maxRetries, CancellationToken ct = default);

    /// <summary>Read a task by id, or <c>null</c>.</summary>
    Task<PlatformQueuedTask?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Visibility-timeout reaper. Resets rows stuck in <c>processing</c>
    /// for longer than <paramref name="visibilityTimeout"/> back to
    /// <c>pending</c> (or <c>dead_letter</c> when retry ceiling reached)
    /// so a worker that died mid-task does not leave zombies forever.
    /// Returns the number of rows reaped. Mirrors
    /// <see cref="IQueuedTaskRepository.ReapStaleProcessingAsync"/>.
    /// </summary>
    Task<int> ReapStaleProcessingAsync(
        TimeSpan visibilityTimeout, int maxRetries, CancellationToken ct = default);
}
